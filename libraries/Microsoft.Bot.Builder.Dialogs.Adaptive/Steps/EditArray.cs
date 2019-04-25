﻿// Licensed under the MIT License.
// Copyright (c) Microsoft Corporation. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace Microsoft.Bot.Builder.Dialogs.Adaptive.Steps
{
    /// <summary>
    /// Lets you modify an array in memory
    /// </summary>
    public class EditArray : DialogCommand
    {
        public enum ArrayChangeType
        {
            /// <summary>
            /// Push item onto the end of the array
            /// </summary>
            Push,

            /// <summary>
            /// Pop the item off the end of the array
            /// </summary>
            Pop,

            /// <summary>
            /// Take an item from the front of the array
            /// </summary>
            Take,

            /// <summary>
            /// Remove the item from the array, regardless of it's location
            /// </summary>
            Remove,

            /// <summary>
            /// Clear the contents of the array
            /// </summary>
            Clear
        }

        [JsonConstructor]
        public EditArray([CallerFilePath] string callerPath = "", [CallerLineNumber] int callerLine = 0)
            : base()
        {
            this.RegisterSourceLocation(callerPath, callerLine);
        }

        protected override string OnComputeId()
        {
            return $"array[{ChangeType + ": " + ArrayProperty}]";
        }

        /// <summary>
        /// type of change being applied
        /// </summary>
        [JsonConverter(typeof(StringEnumConverter))]
        [JsonProperty("changeType")]
        public ArrayChangeType ChangeType { get; set; }

        /// <summary>
        /// Memory expression of the array to manipulate
        /// </summary>Edit
        [JsonProperty("arrayProperty")]
        public string ArrayProperty { get; set; }

        /// <summary>
        /// Memory of the item to put onto the array
        /// </summary>
        [JsonProperty("itemProperty")]
        public string ItemProperty { get; set; }

        public EditArray(ArrayChangeType changeType, string arrayProperty = null, string itemProperty = null)
            : base()
        {
            this.ChangeType = changeType;

            if (!string.IsNullOrEmpty(arrayProperty))
            {
                this.ArrayProperty = arrayProperty;
            }

            if (!string.IsNullOrEmpty(itemProperty))
            {
                this.ItemProperty = itemProperty;
            }
        }

        protected override async Task<DialogTurnResult> OnRunCommandAsync(DialogContext dc, object options = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (options is CancellationToken)
            {
                throw new ArgumentException($"{nameof(options)} cannot be a cancellation token");
            }

            if (string.IsNullOrEmpty(ArrayProperty))
            {
                throw new Exception($"EditArray: \"{ ChangeType }\" operation couldn't be performed because the arrayProperty wasn't specified.");
            }

            var prop = await new TextTemplate(this.ArrayProperty).BindToData(dc.Context, dc.State, (property, data) => data.GetValue<object>(property)).ConfigureAwait(false);
            var array = dc.State.GetValue(prop, new JArray());

            object item = null;
            string serialized = string.Empty;
            object lastResult = null;

            switch (ChangeType)
            {
                case ArrayChangeType.Pop:
                    item = array[array.Count - 1];
                    array.RemoveAt(array.Count - 1);
                    if (!string.IsNullOrEmpty(ItemProperty))
                    {
                        dc.State.SetValue(ItemProperty, item);
                    }
                    lastResult = item;
                    break;
                case ArrayChangeType.Push:
                    EnsureItemProperty();
                    dc.State.TryGetValue<object>(ItemProperty, out item);
                    lastResult = item != null;
                    if ((bool)lastResult)
                    {
                        array.Add(item);
                    }
                    break;
                case ArrayChangeType.Take:
                    if (array.Count == 0)
                    {
                        break;
                    }
                    item = array[0];
                    array.RemoveAt(0);
                    if (!string.IsNullOrEmpty(ItemProperty))
                    {
                        dc.State.SetValue(ItemProperty, item);
                    }
                    lastResult = item;
                    break;
                case ArrayChangeType.Remove:
                    EnsureItemProperty();
                    dc.State.TryGetValue<object>(ItemProperty, out item);
                    if (item != null)
                    {
                        lastResult = false;
                        array.Where(x => x.Value<string>() == item.ToString()).First().Remove();
                    }
                    break;
                case ArrayChangeType.Clear:
                    lastResult = array.Count > 0;
                    array.Clear();
                    break;
            }

            dc.State.SetValue(prop, array);
            return await dc.EndDialogAsync();
        }

        private void EnsureItemProperty()
        {
            if (string.IsNullOrEmpty(ItemProperty))
            {
                throw new Exception($"EditArray: \"{ ChangeType }\" operation couldn't be performed for array \"{ArrayProperty}\" because an itemProperty wasn't specified.");
            }
        }

    }
}
