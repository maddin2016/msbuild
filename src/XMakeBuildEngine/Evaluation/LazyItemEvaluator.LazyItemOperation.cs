﻿using Microsoft.Build.Collections;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Build.BackEnd;
using Microsoft.Build.Construction;
using Microsoft.Build.Execution;
using System.Collections.Immutable;
using Microsoft.Build.Shared;
using Microsoft.Build.Internal;

namespace Microsoft.Build.Evaluation
{
    internal partial class LazyItemEvaluator<P, I, M, D>
    {
        abstract class LazyItemOperation
        {
            protected readonly ProjectItemElement _itemElement;
            protected readonly string _itemType;

            //  If Item1 of tuplee is ItemOperationType.Expression, then Item2 is an ExpressionShredder.ItemExpressionCapture
            //  Otherwise, Item2 is a string (representing either the value or the glob)
            protected readonly ImmutableList<Tuple<ItemOperationType, object>> _operations;

            protected readonly ImmutableDictionary<string, LazyItemList> _referencedItemLists;

            protected readonly LazyItemEvaluator<P, I, M, D> _lazyEvaluator;
            protected readonly EvaluatorData _evaluatorData;
            protected readonly Expander<P, I> _expander;

            //  This is used only when evaluating an expression, which instantiates
            //  the items and then removes them
            protected readonly IItemFactory<I, I> _itemFactory;


            public LazyItemOperation(OperationBuilder builder, LazyItemEvaluator<P, I, M, D> lazyEvaluator)
            {
                _itemElement = builder.ItemElement;
                _itemType = builder.ItemType;
                _operations = builder.Operations.ToImmutable();
                _referencedItemLists = builder.ReferencedItemLists.ToImmutable();

                _lazyEvaluator = lazyEvaluator;
                _evaluatorData = new EvaluatorData(_lazyEvaluator._outerEvaluatorData, itemType => GetReferencedItems(itemType, ImmutableHashSet<string>.Empty));
                _expander = new Expander<P, I>(_evaluatorData, _evaluatorData);

                _itemFactory = new ItemFactoryWrapper(_itemElement, _lazyEvaluator._itemFactory);
            }

            IList<I> GetReferencedItems(string itemType, ImmutableHashSet<string> globsToIgnore)
            {
                LazyItemList itemList;
                if (_referencedItemLists.TryGetValue(itemType, out itemList))
                {
                    return itemList.GetItems(globsToIgnore)
                        .Where(ItemData => ItemData.ConditionResult)
                        .Select(itemData => itemData.Item)
                        .ToList();
                }
                else
                {
                    return ImmutableList<I>.Empty;
                }
            }

            public virtual void Apply(ImmutableList<ItemData>.Builder listBuilder, ImmutableHashSet<string> globsToIgnore)
            {
                var items = SelectItems(listBuilder, globsToIgnore).ToList();
                MutateItems(items);
                SaveItems(items, listBuilder);
            }

            /// <summary>
            /// Produce the items to operate on. For example, create new ones or select existing ones
            /// </summary>
            protected virtual ICollection<I> SelectItems(ImmutableList<ItemData>.Builder listBuilder, ImmutableHashSet<string> globsToIgnore)
            {
                return listBuilder.Select(itemData => itemData.Item).ToList();
            }

            protected virtual void MutateItems(ICollection<I> items) { }

            protected virtual void SaveItems(ICollection<I> items, ImmutableList<ItemData>.Builder listBuilder) { }

            protected void DecorateItemsWithMetadata(ICollection<I> items, ImmutableList<ProjectMetadataElement> metadata)
            {
                if (metadata.Any())
                {
                    ////////////////////////////////////////////////////
                    // UNDONE: Implement batching here.
                    //
                    // We want to allow built-in metadata in metadata values here. 
                    // For example, so that an Idl file can specify that its Tlb output should be named %(Filename).tlb.
                    // 
                    // In other words, we want batching. However, we won't need to go to the trouble of using the regular batching code!
                    // That's because that code is all about grouping into buckets of similar items. In this context, we're not
                    // invoking a task, and it's fine to process each item individually, which will always give the correct results.
                    //
                    // For the CTP, to make the minimal change, we will not do this quite correctly.
                    //
                    // We will do this:
                    // -- check whether any metadata values or their conditions contain any bare built-in metadata expressions,
                    //    or whether they contain any custom metadata && the Include involved an @(itemlist) expression.
                    // -- if either case is found, we go ahead and evaluate all the metadata separately for each item.
                    // -- otherwise we can do the old thing (evaluating all metadata once then applying to all items)
                    // 
                    // This algorithm gives the correct results except when:
                    // -- batchable expressions exist on the include, exclude, or condition on the item element itself
                    //
                    // It means that 99% of cases still go through the old code, which is best for the CTP.
                    // When we ultimately implement this correctly, we should make sure we optimize for the case of very many items
                    // and little metadata, none of which varies between items.
                    List<string> values = new List<string>(metadata.Count * 2);

                    foreach (ProjectMetadataElement metadatumElement in metadata)
                    {
                        values.Add(metadatumElement.Value);
                        values.Add(metadatumElement.Condition);
                    }

                    ItemsAndMetadataPair itemsAndMetadataFound = ExpressionShredder.GetReferencedItemNamesAndMetadata(values);

                    bool needToProcessItemsIndividually = false;

                    if (itemsAndMetadataFound.Metadata != null && itemsAndMetadataFound.Metadata.Values.Count > 0)
                    {
                        // If there is bare metadata of any kind, and the Include involved an item list, we should
                        // run items individually, as even non-built-in metadata might differ between items

                        if (_referencedItemLists.Count >= 0)
                        {
                            needToProcessItemsIndividually = true;
                        }
                        else
                        {
                            // If there is bare built-in metadata, we must always run items individually, as that almost
                            // always differs between items.

                            // UNDONE: When batching is implemented for real, we need to make sure that
                            // item definition metadata is included in all metadata operations during evaluation
                            if (itemsAndMetadataFound.Metadata.Values.Count > 0)
                            {
                                needToProcessItemsIndividually = true;
                            }
                        }
                    }

                    if (needToProcessItemsIndividually)
                    {
                        foreach (I item in items)
                        {
                            _expander.Metadata = item;

                            foreach (ProjectMetadataElement metadatumElement in metadata)
                            {
#if FEATURE_MSBUILD_DEBUGGER
                                //if (DebuggerManager.DebuggingEnabled)
                                //{
                                //    DebuggerManager.PulseState(metadatumElement.Location, _itemPassLocals);
                                //}
#endif

                                if (!EvaluateCondition(metadatumElement, ExpanderOptions.ExpandAll, ParserOptions.AllowAll, _expander, _lazyEvaluator))
                                {
                                    continue;
                                }

                                string evaluatedValue = _expander.ExpandIntoStringLeaveEscaped(metadatumElement.Value, ExpanderOptions.ExpandAll, metadatumElement.Location);

                                item.SetMetadata(metadatumElement, evaluatedValue);
                            }
                        }

                        // End of legal area for metadata expressions.
                        _expander.Metadata = null;
                    }

                    // End of pseudo batching
                    ////////////////////////////////////////////////////
                    // Start of old code
                    else
                    {
                        // Metadata expressions are allowed here.
                        // Temporarily gather and expand these in a table so they can reference other metadata elements above.
                        EvaluatorMetadataTable metadataTable = new EvaluatorMetadataTable(_itemType);
                        _expander.Metadata = metadataTable;

                        // Also keep a list of everything so we can get the predecessor objects correct.
                        List<Pair<ProjectMetadataElement, string>> metadataList = new List<Pair<ProjectMetadataElement, string>>();

                        foreach (ProjectMetadataElement metadatumElement in metadata)
                        {
                            // Because of the checking above, it should be safe to expand metadata in conditions; the condition
                            // will be true for either all the items or none
                            if (!EvaluateCondition(metadatumElement, ExpanderOptions.ExpandAll, ParserOptions.AllowAll, _expander, _lazyEvaluator))
                            {
                                continue;
                            }

#if FEATURE_MSBUILD_DEBUGGER
                            //if (DebuggerManager.DebuggingEnabled)
                            //{
                            //    DebuggerManager.PulseState(metadatumElement.Location, _itemPassLocals);
                            //}
#endif

                            string evaluatedValue = _expander.ExpandIntoStringLeaveEscaped(metadatumElement.Value, ExpanderOptions.ExpandAll, metadatumElement.Location);

                            metadataTable.SetValue(metadatumElement, evaluatedValue);
                            metadataList.Add(new Pair<ProjectMetadataElement, string>(metadatumElement, evaluatedValue));
                        }

                        // Apply those metadata to each item
                        // Note that several items could share the same metadata objects

                        // Set all the items at once to make a potential copy-on-write optimization possible.
                        // This is valuable in the case where one item element evaluates to
                        // many items (either by semicolon or wildcards)
                        // and that item also has the same piece/s of metadata for each item.
                        _itemFactory.SetMetadata(metadataList, items);

                        // End of legal area for metadata expressions.
                        _expander.Metadata = null;
                    }
                }
            }

            /// <summary>
            /// Collects all the items of this item element's type that match the items (represented as operations)
            /// </summary>
            protected ICollection<I> SelectItemsMatchingItemSpec(ImmutableList<ItemData>.Builder listBuilder, IElementLocation elementLocation)
            {
                //  TODO: Figure out case sensitivity on non-Windows OS's
                HashSet<string> itemValuesToMatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                List<string> globsToMatch = new List<string>();

                foreach (var operation in _operations)
                {
                    if (operation.Item1 == ItemOperationType.Expression)
                    {
                        //  TODO: consider optimizing the case where an item element removes all items of its
                        //  item type, for example:
                        //      <Compile Remove="@(Compile)" />
                        //  In this case we could avoid evaluating previous versions of the list entirely
                        bool throwaway;
                        var itemsFromExpression = _expander.ExpandExpressionCaptureIntoItems(
                            (ExpressionShredder.ItemExpressionCapture)operation.Item2, _evaluatorData, _itemFactory, ExpanderOptions.ExpandItems,
                            false /* do not include null expansion results */, out throwaway, elementLocation);

                        foreach (var item in itemsFromExpression)
                        {
                            itemValuesToMatch.Add(item.EvaluatedInclude);
                        }
                    }
                    else if (operation.Item1 == ItemOperationType.Value)
                    {
                        itemValuesToMatch.Add((string)operation.Item2);
                    }
                    else if (operation.Item1 == ItemOperationType.Glob)
                    {
                        string glob = (string)operation.Item2;
                        globsToMatch.Add(glob);
                    }
                    else
                    {
                        throw new InvalidOperationException(operation.Item1.ToString());
                    }
                }

                var globbingMatcher = EngineFileUtilities.GetMatchTester(globsToMatch);

                return listBuilder
                    .Select(itemData => itemData.Item)
                    .Where(item => itemValuesToMatch.Contains(item.EvaluatedInclude) || globbingMatcher(item.EvaluatedInclude))
                    .ToImmutableHashSet();
            }
        }
    }
}
