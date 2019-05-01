﻿using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Microsoft.Bot.Builder.Dialogs.Debugging
{
    public sealed class DebugAdapter : DebugTransport, IMiddleware, DebugSupport.IDebugger
    {
        private readonly CancellationTokenSource cancellationToken = new CancellationTokenSource();

        private readonly ICodeModel codeModel;
        private readonly IDataModel dataModel;
        private readonly Source.IRegistry registry;
        private readonly IBreakpoints breakpoints;
        private readonly IEvents events;
        private readonly Action terminate;

        // lifetime scoped to IMiddleware.OnTurnAsync
        private readonly ConcurrentDictionary<string, ThreadModel> threadByTurnId = new ConcurrentDictionary<string, ThreadModel>();
        private readonly Identifier<ThreadModel> threads = new Identifier<ThreadModel>();

        private sealed class ThreadModel
        {
            public ThreadModel(ITurnContext turnContext, ICodeModel codeModel)
            {
                TurnContext = turnContext;
                CodeModel = codeModel;
            }
            public ITurnContext TurnContext { get; }
            public ICodeModel CodeModel { get; }
            public string Name => TurnContext.Activity.Text;
            public IReadOnlyList<ICodePoint> Frames => CodeModel.PointsFor(LastContext, LastItem, LastMore);
            public RunModel Run { get; } = new RunModel();
            public Identifier<ICodePoint> FrameCodes { get; } = new Identifier<ICodePoint>();
            public Identifier<object> ValueCodes { get; } = new Identifier<object>();
            public DialogContext LastContext { get; set; }
            public object LastItem { get; set; }
            public string LastMore { get; set; }
        }

        public enum Phase { Started, Continue, Next, Step, Breakpoint, Pause, Exited };

        public sealed class RunModel
        {
            public Phase? PhaseSent { get; set; }
            public Phase Phase { get; set; } = Phase.Started;
            public object Gate { get; } = new object();

            public void Post(Phase what)
            {
                Monitor.Enter(Gate);
                try
                {
                    Phase = what;
                    Monitor.Pulse(Gate);
                }
                finally
                {
                    Monitor.Exit(Gate);
                }
            }
        }

        private ulong EncodeValue(ThreadModel thread, object value)
        {
            if (dataModel.IsScalar(value))
            {
                return 0;
            }

            var threadCode = threads[thread];
            var valueCode = thread.ValueCodes.Add(value);
            return Identifier.Encode(threadCode, valueCode);
        }

        private void DecodeValue(ulong variablesReference, out ThreadModel thread, out object value)
        {
            Identifier.Decode(variablesReference, out var threadCode, out var valueCode);
            thread = this.threads[threadCode];
            value = thread.ValueCodes[valueCode];
        }

        private ulong EncodeFrame(ThreadModel thread, ICodePoint frame)
        {
            var threadCode = threads[thread];
            var valueCode = thread.FrameCodes.Add(frame);
            return Identifier.Encode(threadCode, valueCode);
        }

        private void DecodeFrame(ulong frameCode, out ThreadModel thread, out ICodePoint frame)
        {
            Identifier.Decode(frameCode, out var threadCode, out var valueCode);
            thread = this.threads[threadCode];
            frame = thread.FrameCodes[valueCode];
        }

        private readonly Task task;

        public DebugAdapter(int port, Source.IRegistry registry, IBreakpoints breakpoints, Action terminate, IEvents events = null,ICodeModel codeModel = null, IDataModel dataModel = null, ILogger logger = null, ICoercion coercion = null)
            : base(logger)
        {
            this.events = events ?? new Events();
            this.codeModel = codeModel ?? new CodeModel();
            this.dataModel = dataModel ?? new DataModel(coercion ?? new Coercion());
            this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
            this.breakpoints = breakpoints ?? throw new ArgumentNullException(nameof(breakpoints));
            this.terminate = terminate ?? new Action(() => Environment.Exit(0));
            this.task = ListenAsync(new IPEndPoint(IPAddress.Any, port), cancellationToken.Token);
        }

        public async Task DisposeAsync()
        {
            this.cancellationToken.Cancel();
            using (this.cancellationToken)
            using (this.task)
            {
                await this.task.ConfigureAwait(false);
            }
        }

        async Task IMiddleware.OnTurnAsync(ITurnContext turnContext, NextDelegate next, CancellationToken cancellationToken)
        {
            var thread = new ThreadModel(turnContext, codeModel);
            var threadId = threads.Add(thread);
            threadByTurnId.TryAdd(TurnIdFor(turnContext), thread);
            try
            {
                thread.Run.Post(Phase.Started);
                await UpdateThreadPhaseAsync(thread, null, cancellationToken).ConfigureAwait(false);

                DebugSupport.IDebugger trace = this;
                turnContext.TurnState.Add(trace);
                await next(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                thread.Run.Post(Phase.Exited);
                await UpdateThreadPhaseAsync(thread, null, cancellationToken).ConfigureAwait(false);

                threadByTurnId.TryRemove(TurnIdFor(turnContext), out var ignored);
                threads.Remove(thread);
            }
        }

        private static string TurnIdFor(ITurnContext turnContext)
        {
            return $"{turnContext.Activity.ChannelId}-{turnContext.Activity.Id}";
        }

        async Task DebugSupport.IDebugger.StepAsync(DialogContext context, object item, string more, CancellationToken cancellationToken)
        {
            try
            {
                await OutputAsync($"Step: {codeModel.NameFor(item)} {more}", item, cancellationToken).ConfigureAwait(false);

                await UpdateBreakpointsAsync(cancellationToken).ConfigureAwait(false);

                if (threadByTurnId.TryGetValue(TurnIdFor(context.Context), out ThreadModel thread))
                {
                    thread.LastContext = context;
                    thread.LastItem = item;
                    thread.LastMore = more;

                    var run = thread.Run;
                    if (breakpoints.IsBreakPoint(item) && events[more])
                    {
                        run.Post(Phase.Breakpoint);
                    }

                    // TODO: implement asynchronous condition variables
                    Monitor.Enter(run.Gate);
                    try
                    {
                        // TODO: remove synchronous waits
                        UpdateThreadPhaseAsync(thread, item, cancellationToken).GetAwaiter().GetResult();

                        // while the stopped condition is true, atomically release the mutex
                        while (!(run.Phase == Phase.Started || run.Phase == Phase.Continue || run.Phase == Phase.Next))
                        {
                            Monitor.Wait(run.Gate);
                        }

                        // started is just use the signal to Visual Studio Code that there is a new thread
                        if (run.Phase == Phase.Started)
                        {
                            run.Phase = Phase.Continue;
                        }

                        // TODO: remove synchronous waits
                        UpdateThreadPhaseAsync(thread, item, cancellationToken).GetAwaiter().GetResult();

                        // allow one step to progress since next was requested
                        if (run.Phase == Phase.Next)
                        {
                            run.Phase = Phase.Step;
                        }
                    }
                    finally
                    {
                        Monitor.Exit(run.Gate);
                    }
                }
                else
                {
                    this.logger.LogError($"thread context not found");
                }
            }
            catch (Exception error)
            {
                this.logger.LogError(error, error.Message);
            }
        }

        private async Task UpdateBreakpointsAsync(CancellationToken cancellationToken)
        {
            var breakpoints = this.breakpoints.ApplyUpdates();
            foreach (var breakpoint in breakpoints)
            {
                if (breakpoint.verified)
                {
                    var item = this.breakpoints.ItemFor(breakpoint);
                    await OutputAsync($"Set breakpoint at {codeModel.NameFor(item)}", item, cancellationToken).ConfigureAwait(false);
                }

                var body = new { reason = "changed", breakpoint };
                await SendAsync(Protocol.Event.From(NextSeq, "breakpoint", body), cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task UpdateThreadPhaseAsync(ThreadModel thread, object item, CancellationToken cancellationToken)
        {
            var run = thread.Run;
            if (run.Phase == run.PhaseSent)
            {
                return;
            }

            var phase = run.Phase;
            var suffix = item != null ? $" at {codeModel.NameFor(item)}" : string.Empty;
            var description = $"'{thread.Name}' is {phase}{suffix}";

            await OutputAsync(description, item, cancellationToken).ConfigureAwait(false);

            var threadId = this.threads[thread];

            if (phase == Phase.Next)
            {
                phase = Phase.Continue;
            }

            string reason = phase.ToString().ToLower();

            if (phase == Phase.Started || phase == Phase.Exited)
            {
                await SendAsync(Protocol.Event.From(NextSeq, "thread", new { threadId, reason }), cancellationToken).ConfigureAwait(false);
            }
            else if (phase == Phase.Continue)
            {
                await SendAsync(Protocol.Event.From(NextSeq, "continue", new { threadId, allThreadsContinued = false }), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var body = new
                {
                    reason,
                    description,
                    threadId,
                    text = description,
                    preserveFocusHint = false,
                    allThreadsStopped = false,
                };

                await SendAsync(Protocol.Event.From(NextSeq, "stopped", body), cancellationToken).ConfigureAwait(false);
            }

            run.PhaseSent = run.Phase;
        }

        private async Task SendAsync(Protocol.Message message, CancellationToken cancellationToken)
        {
            var token = JToken.FromObject(message, new JsonSerializer() { NullValueHandling = NullValueHandling.Include });
            await SendAsync(token, cancellationToken).ConfigureAwait(false);
        }

        private async Task OutputAsync(string text, object item, CancellationToken cancellationToken)
        {
            bool found = this.registry.TryGetValue(item, out var range);

            var body = new
            {
                output = text + Environment.NewLine,
                source = found ? new Protocol.Source(range.Path) : null,
                line = found ? (int?)range.Start.LineIndex : null,
            };

            await SendAsync(Protocol.Event.From(NextSeq, "output", body), cancellationToken).ConfigureAwait(false);
        }

        private int sequence = 0;
        private int NextSeq => Interlocked.Increment(ref sequence);

        private Protocol.Capabilities MakeCapabilities()
        {
            // TODO: there is a "capabilities" event for dynamic updates, but exceptionBreakpointFilters does not seem to be dynamically updateable
            return new Protocol.Capabilities()
            {
                supportsConfigurationDoneRequest = true,
                supportsSetVariable = true,
                supportsEvaluateForHovers = true,
                supportsFunctionBreakpoints = true,
                exceptionBreakpointFilters = this.events.Filters,
                supportTerminateDebuggee = this.terminate != null,
                supportsTerminateRequest = this.terminate != null,
            };
        }

        private async Task<Protocol.Message> DispatchAsync(Protocol.Message message, CancellationToken cancellationToken)
        {
            if (message is Protocol.Request<Protocol.Initialize> initialize)
            {
                var body = MakeCapabilities();
                var response = Protocol.Response.From(NextSeq, initialize, body);
                await SendAsync(response, cancellationToken).ConfigureAwait(false);
                return Protocol.Event.From(NextSeq, "initialized", new { });
            }
            else if (message is Protocol.Request<Protocol.Launch> launch)
            {
                return Protocol.Response.From(NextSeq, launch, new { });
            }
            else if (message is Protocol.Request<Protocol.Attach> attach)
            {
                return Protocol.Response.From(NextSeq, attach, new { });
            }
            else if (message is Protocol.Request<Protocol.SetBreakpoints> setBreakpoints)
            {
                var arguments = setBreakpoints.arguments;
                var file = Path.GetFileName(arguments.source.path);
                await OutputAsync($"Set breakpoints for {file}", null, cancellationToken).ConfigureAwait(false);

                var breakpoints = this.breakpoints.SetBreakpoints(arguments.source, arguments.breakpoints);
                foreach (var breakpoint in breakpoints)
                {
                    if (breakpoint.verified)
                    {
                        var item = this.breakpoints.ItemFor(breakpoint);
                        await OutputAsync($"Set breakpoint at {codeModel.NameFor(item)}", item, cancellationToken).ConfigureAwait(false);
                    }
                }

                return Protocol.Response.From(NextSeq, setBreakpoints, new { breakpoints });
            }
            else if (message is Protocol.Request<Protocol.SetFunctionBreakpoints> setFunctionBreakpoints)
            {
                var arguments = setFunctionBreakpoints.arguments;
                await OutputAsync($"Set function breakpoints.", null, cancellationToken).ConfigureAwait(false);
                var breakpoints = this.breakpoints.SetBreakpoints(arguments.breakpoints);
                foreach (var breakpoint in breakpoints)
                {
                    if (breakpoint.verified)
                    {
                        var item = this.breakpoints.ItemFor(breakpoint);
                        await OutputAsync($"Set breakpoint at {codeModel.NameFor(item)}", item, cancellationToken).ConfigureAwait(false);
                    }
                }

                return Protocol.Response.From(NextSeq, setFunctionBreakpoints, new { breakpoints });
            }
            else if (message is Protocol.Request<Protocol.SetExceptionBreakpoints> setExceptionBreakpoints)
            {
                var arguments = setExceptionBreakpoints.arguments;
                this.events.Reset(arguments.filters);

                return Protocol.Response.From(NextSeq, setExceptionBreakpoints, new { });
            }
            else if (message is Protocol.Request<Protocol.Threads> threads)
            {
                var body = new
                {
                    threads = this.threads.Select(t => new { id = t.Key, name = t.Value.Name }).ToArray()
                };

                return Protocol.Response.From(NextSeq, threads, body);
            }
            else if (message is Protocol.Request<Protocol.StackTrace> stackTrace)
            {
                var arguments = stackTrace.arguments;
                var thread = this.threads[arguments.threadId];

                var frames = thread.Frames;
                var stackFrames = new List<Protocol.StackFrame>();
                foreach (var frame in frames)
                {
                    var stackFrame = new Protocol.StackFrame()
                    {
                        id = EncodeFrame(thread, frame),
                        name = frame.Name
                    };

                    if (this.registry.TryGetValue(frame.Item, out var range))
                    {
                        SourceMap.Assign(stackFrame, range);
                    }

                    stackFrames.Add(stackFrame);
                }

                return Protocol.Response.From(NextSeq, stackTrace, new { stackFrames });
            }
            else if (message is Protocol.Request<Protocol.Scopes> scopes)
            {
                var arguments = scopes.arguments;
                DecodeFrame(arguments.frameId, out var thread, out var frame);
                const bool expensive = false;

                var body = new
                {
                    scopes = new[]
                    {
                        new { expensive, name = frame.Name, variablesReference = EncodeValue(thread, frame.Data) }
                    }
                };

                return Protocol.Response.From(NextSeq, scopes, body);
            }
            else if (message is Protocol.Request<Protocol.Variables> vars)
            {
                var arguments = vars.arguments;
                DecodeValue(arguments.variablesReference, out var thread, out var context);

                var names = this.dataModel.Names(context);

                var body = new
                {
                    variables = (from name in names
                                 let value = dataModel[context, name]
                                 let variablesReference = EncodeValue(thread, value)
                                 select new { name = dataModel.ToString(name), value = dataModel.ToString(value), variablesReference }
                                ).ToArray()
                };

                return Protocol.Response.From(NextSeq, vars, body);
            }
            else if (message is Protocol.Request<Protocol.SetVariable> setVariable)
            {
                var arguments = setVariable.arguments;
                DecodeValue(arguments.variablesReference, out var thread, out var context);

                var value = this.dataModel[context, arguments.name] = JToken.Parse(arguments.value);

                var body = new
                {
                    value = dataModel.ToString(value),
                    variablesReference = EncodeValue(thread, value)
                };

                return Protocol.Response.From(NextSeq, setVariable, body);
            }
            else if (message is Protocol.Request<Protocol.Evaluate> evaluate)
            {
                var arguments = evaluate.arguments;
                DecodeFrame(arguments.frameId, out var thread, out var frame);
                var expression = arguments.expression.Trim('"');
                var result = frame.Evaluate(expression);
                if (result != null)
                {
                    var body = new
                    {
                        result = dataModel.ToString(result),
                        variablesReference = EncodeValue(thread, result),
                    };

                    return Protocol.Response.From(NextSeq, evaluate, body);
                }
                else
                {
                    return Protocol.Response.Fail(NextSeq, evaluate, string.Empty);
                }
            }
            else if (message is Protocol.Request<Protocol.Continue> cont)
            {
                bool found = this.threads.TryGetValue(cont.arguments.threadId, out var thread);
                if (found)
                {
                    thread.Run.Post(Phase.Continue);
                }

                return Protocol.Response.From(NextSeq, cont, new { allThreadsContinued = false });
            }
            else if (message is Protocol.Request<Protocol.Pause> pause)
            {
                bool found = this.threads.TryGetValue(pause.arguments.threadId, out var thread);
                if (found)
                {
                    thread.Run.Post(Phase.Pause);
                }

                return Protocol.Response.From(NextSeq, pause, new { });
            }
            else if (message is Protocol.Request<Protocol.Next> next)
            {
                bool found = this.threads.TryGetValue(next.arguments.threadId, out var thread);
                if (found)
                {
                    thread.Run.Post(Phase.Next);
                }

                return Protocol.Response.From(NextSeq, next, new { });
            }
            else if (message is Protocol.Request<Protocol.Terminate> terminate)
            {
                if (this.terminate != null)
                {
                    this.terminate();
                }

                return Protocol.Response.From(NextSeq, terminate, new { });
            }
            else if (message is Protocol.Request<Protocol.Disconnect> disconnect)
            {
                var arguments = disconnect.arguments;
                if (arguments.terminateDebuggee && this.terminate != null)
                {
                    this.terminate();
                }

                // if attach, possibly run all threads
                return Protocol.Response.From(NextSeq, disconnect, new { });
            }
            else if (message is Protocol.Request request)
            {
                return Protocol.Response.From(NextSeq, request, new { });
            }
            else if (message is Protocol.Event @event)
            {
                throw new NotImplementedException();
            }
            else
            {
                throw new NotImplementedException();
            }
        }

        protected override async Task AcceptAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var token = await ReadAsync(cancellationToken).ConfigureAwait(false);
                    var request = Protocol.Parse(token);
                    Protocol.Message message;
                    try
                    {
                        message = await DispatchAsync(request, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception error)
                    {
                        message = Protocol.Response.Fail(NextSeq, request, error.Message);
                    }

                    await SendAsync(message, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    this.logger.LogError(error, error.Message);
                    throw;
                }
            }
        }
    }
}
