﻿// Licensed under the MIT License.
// Copyright (c) Microsoft Corporation. All rights reserved.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Bot.Schema;

namespace Microsoft.Bot.Builder.Dialogs.Adaptive.Input
{
    /// <summary>
    /// Generic wrapper around prompts to abstract non-declarative prompts into declarative style input classes.
    /// </summary>
    /// <typeparam name="TPrompt">Prompt type being wrapped</typeparam>
    /// <typeparam name="TValue">Type of the value that the prompt will store and get to and from memory</typeparam>
    public class InputWrapper<TPrompt, TValue> : DialogCommand, IDialogDependencies where TPrompt : IDialog, new()
    {
        protected TPrompt prompt;

        /// <summary>
        /// Activity to send to the user
        /// </summary>
        public ITemplate<Activity> Prompt { get; set; }

        /// <summary>
        /// Activity template for retrying prompt
        /// </summary>
        public ITemplate<Activity> RetryPrompt { get; set; }

        /// <summary>
        /// Activity template to send to the user whenever the value provided is invalid
        /// </summary>
        public ITemplate<Activity> InvalidPrompt { get; set; }

        /// <summary>
        /// The property from memory to pass to the calling dialog and to set the return value to.
        /// </summary>
        public override string Property
        {
            get
            {
                return OutputBinding;
            }
            set
            {
                InputBindings["value"] = value;
                OutputBinding = value;
            }
        }

        public bool AlwaysPrompt { get; set; } = false;

        public InputWrapper() : base()
        {
            prompt = CreatePrompt();
        }

        protected override async Task<DialogTurnResult> OnRunCommandAsync(DialogContext dc, object options = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (options is CancellationToken)
            {
                throw new ArgumentException($"{nameof(options)} cannot be a cancellation token");
            }

            // Check value in state and only call if missing and Property specified, or if required by AlwaysPrompt
            var hasValue = Property == null ? false : dc.State.HasValue(Property);

            if (hasValue == false || AlwaysPrompt)
            {
                if (Prompt == null)
                {
                    throw new ArgumentNullException(nameof(Activity));
                }

                var prompt = await Prompt.BindToData(dc.Context, dc.State).ConfigureAwait(false);
                var retryPrompt = RetryPrompt == null ? prompt : await RetryPrompt.BindToData(dc.Context, dc.State).ConfigureAwait(false);

                return await dc.PromptAsync(this.prompt.Id, new PromptOptions() { Prompt = prompt, RetryPrompt = retryPrompt }, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                return await dc.EndDialogAsync(cancellationToken: cancellationToken);
            }
        }

        public override List<IDialog> ListDependencies()
        {
            // Update inner prompt id before returning
            prompt.Id = Id + ":prompt";
            return new List<IDialog>() { prompt };
        }

        /// <summary>
        /// We allow subclasses to provide special creation of the TPrompt wrapped prompt. 
        /// We provide a default implementation that suits more cases except when validation needs
        /// to be customized at construction time
        /// </summary>
        /// <returns></returns>
        protected virtual TPrompt CreatePrompt()
        {
            return new TPrompt();
        }
    }
}
