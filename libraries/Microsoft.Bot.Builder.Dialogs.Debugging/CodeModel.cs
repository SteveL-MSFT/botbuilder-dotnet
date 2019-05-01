﻿using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Microsoft.Bot.Builder.Dialogs.Debugging
{
    public interface ICodeModel
    {
        string NameFor(object item);
        IReadOnlyList<ICodePoint> PointsFor(DialogContext dialogContext, object item, string more);
    }

    public sealed class CodeModel : ICodeModel
    {
        string ICodeModel.NameFor(object item)
        {
            var type = item.GetType().Name;
            if (item is IDialog dialog)
            {
                var name = dialog.Id;
                return name.StartsWith(type) ? name : $"{type}:{name}";
            }

            return type;
        }

        IReadOnlyList<ICodePoint> ICodeModel.PointsFor(DialogContext dialogContext, object item, string more)
        {
            var frames = new List<CodePoint>();

            if (item != null)
            {
                var frame = new CodePoint(this, dialogContext, item, more);
                frames.Add(frame);
            }

            while (dialogContext != null)
            {
                foreach (var instance in dialogContext.Stack)
                {
                    var dialog = dialogContext.FindDialog(instance.Id);
                    var frame = new CodePoint(this, dialogContext, dialog, null);
                    frames.Add(frame);
                }

                dialogContext = dialogContext.Parent;
            }

            return frames;
        }
    }

    public interface ICodePoint
    {
        object Item { get; }
        string More { get; }
        string Name { get; }
        object Data { get; }
        object Evaluate(string expression);
    }

    public sealed class CodePoint : ICodePoint
    {
        public CodePoint(ICodeModel codeModel, DialogContext dialogContext, object item, string more)
        {
            CodeModel = codeModel ?? throw new ArgumentNullException(nameof(codeModel));
            DialogContext = dialogContext ?? throw new ArgumentNullException(nameof(dialogContext));
            Item = item ?? throw new ArgumentNullException(nameof(item));
            More = more;
        }

        private ICodeModel CodeModel { get; }
        private DialogContext DialogContext { get; }
        public object Item { get; }
        public string More { get; }

        public string Name => CodeModel.NameFor(Item) + (More != null ? ":" + More : string.Empty);
        public object Data
        {
            get
            {
                var state = DialogContext.State;
                return new
                {
                    user = state.User,
                    conversation = state.Conversation,
                    dialog = DialogContext.ActiveDialog != null ? state.Dialog : null,
                    turn = state.Turn,
                    tags = DialogContext.ActiveTags,
                };
            }
        }

        public override string ToString() => Name;

        object ICodePoint.Evaluate(string expression) => DialogContext.State.GetValue<object>(expression);
    }
}
