﻿// Licensed under the MIT License.
// Copyright (c) Microsoft Corporation. All rights reserved.

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Bot.Schema;
using Newtonsoft.Json;

namespace Microsoft.Bot.Builder.Dialogs.Adaptive.Steps
{
    /// <summary>
    /// Write log activity to console log
    /// </summary>
    public class LogStep : DialogCommand
    {
        /// <summary>
        /// LG expression to log
        /// </summary>
        [JsonProperty("text")]
        public ITextTemplate Text { get; set; }

        /// <summary>
        /// If set to true a TraceActivity will be sent in addition to console log
        /// </summary>
        [JsonProperty("traceActivity")]
        public bool TraceActivity { get; set; } = false;

        [JsonConstructor]
        public LogStep(string text = null, [CallerFilePath] string callerPath = "", [CallerLineNumber] int callerLine = 0)
        {
            this.RegisterSourceLocation(callerPath, callerLine);
            if (text != null)
            {
                Text = new TextTemplate(text);
            }
        }

        protected override async Task<DialogTurnResult> OnRunCommandAsync(DialogContext dc, object options = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            var text = await Text.BindToData(dc.Context, dc.State, (property, data) => data.GetValue<object>(property)).ConfigureAwait(false);

            System.Diagnostics.Trace.TraceInformation(text);

            if (this.TraceActivity)
            {
                var traceActivity = Activity.CreateTraceActivity("Log", "Text", text);
                await dc.Context.SendActivityAsync(traceActivity, cancellationToken).ConfigureAwait(false);
            }

            return await dc.EndDialogAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        protected override string OnComputeId()
        {
            return $"LogStep({Text?.ToString()})";
        }
    }
}
