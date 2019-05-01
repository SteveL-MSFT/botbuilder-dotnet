﻿using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Microsoft.Bot.Builder.Dialogs.Adaptive.Input
{
    public class DateTimeInput : InputWrapper<DateTimePrompt, IList<DateTimeResolution>>
    {
        public DateTimeInput([CallerFilePath] string callerPath = "", [CallerLineNumber] int callerLine = 0)
        {
            this.RegisterSourceLocation(callerPath, callerLine);
        }

        protected override DateTimePrompt CreatePrompt()
        {
            return new DateTimePrompt(null, new PromptValidator<IList<DateTimeResolution>>(async (promptContext, cancel) =>
            {
                if (!promptContext.Recognized.Succeeded)
                {
                    return false;
                }
                
                if (InvalidPrompt != null)
                {
                    var invalid = await InvalidPrompt.BindToData(promptContext.Context, promptContext.State).ConfigureAwait(false);
                    if (invalid != null)
                    {
                        await promptContext.Context.SendActivityAsync(invalid).ConfigureAwait(false);
                    }
                }

                return true;
            }));
        }

        protected override string OnComputeId()
        {
            return $"DateTimeInput[{BindingPath()}]";
        }
    }
}
