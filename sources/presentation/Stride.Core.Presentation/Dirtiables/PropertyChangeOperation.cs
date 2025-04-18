// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Reflection;
using Stride.Core.Transactions;

namespace Stride.Core.Presentation.Dirtiables;

public class PropertyChangeOperation : DirtyingOperation, IMergeableOperation
{
    private readonly bool nonPublic;
    private object? container;
    private object? previousValue;

    public PropertyChangeOperation(string propertyName, object container, object? previousValue, IEnumerable<IDirtiable> dirtiables, bool nonPublic = false)
        : base(dirtiables)
    {
        ArgumentNullException.ThrowIfNull(propertyName);
        ArgumentNullException.ThrowIfNull(container);

        PropertyName = propertyName;
        ContainerType = container.GetType();
        this.container = container;
        this.previousValue = previousValue;
        this.nonPublic = nonPublic;
    }

    /// <summary>
    /// Gets the type of the property's container.
    /// </summary>
    public Type ContainerType { get; }

    /// <summary>
    /// Gets the name of the property affected by the change.
    /// </summary>
    public string PropertyName { get; }

    /// <inheritdoc/>
    public virtual bool CanMerge(IMergeableOperation otherOperation)
    {
        if (otherOperation is not PropertyChangeOperation operation)
            return false;

        if (container != operation.container)
            return false;

        if (!HasSameDirtiables(operation))
            return false;

        return true;
    }

    /// <inheritdoc/>
    public virtual void Merge(Operation otherOperation)
    {
        // Nothing to do: we keep our current previousValue and we do not store the newValue.
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return $"{{{nameof(PropertyChangeOperation)}: {ContainerType.Name}.{PropertyName}}}";
    }

    /// <inheritdoc/>
    protected override void FreezeContent()
    {
        container = null;
        previousValue = null;
    }

    /// <inheritdoc/>
    protected override void Undo()
    {
        var flags = nonPublic ? BindingFlags.NonPublic | BindingFlags.Instance : BindingFlags.Public | BindingFlags.Instance;
        var propertyInfo = ContainerType.GetProperty(PropertyName, flags);
        var value = previousValue;
        previousValue = propertyInfo?.GetValue(container);
        propertyInfo?.SetValue(container, value);
    }

    /// <inheritdoc/>
    protected override void Redo()
    {
        // Once we have un-done, the previous value is updated so Redo is just Undoing the Undo
        Undo();
    }
}
