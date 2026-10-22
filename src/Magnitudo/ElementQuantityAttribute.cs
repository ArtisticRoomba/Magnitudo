namespace Magnitudo;

/// <summary>
/// Declares the quantity of the elements in an array or indexable container.
/// </summary>
/// <remarks>
/// <para>
/// The quantity belongs to the elements, not to the container. The analyzer applies it to array element access
/// and to indexers. The attribute has no runtime effect.
/// </para>
/// <para>
/// Within one project, an element alias such as <c>Metre[]</c> or <c>List&lt;Metre&gt;</c> is enough. The alias
/// compiles to its primitive type, so it does not appear in metadata. Use this attribute where the elements must
/// keep their quantity across assembly boundaries.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [ElementQuantity("length")]
/// public float[] Heights { get; }
///
/// [return: ElementQuantity("length")]
/// public static float[] ReadPath() => new float[3];
/// </code>
/// </example>
[AttributeUsage(
    AttributeTargets.Field |
    AttributeTargets.Property |
    AttributeTargets.Parameter |
    AttributeTargets.ReturnValue)]
public sealed class ElementQuantityAttribute : Attribute
{
    /// <summary>
    ///     Initializes the attribute with the stable ID of the catalog quantity of the elements.
    /// </summary>
    /// <param name="id">The quantity ID, as written in the first column of a <c>*.magnitudo</c> catalog line.</param>
    /// <exception cref="ArgumentNullException"><paramref name="id" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="id" /> is empty or whitespace.</exception>
    public ElementQuantityAttribute(string id)
    {
        if (id is null)
            throw new ArgumentNullException(nameof(id));
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("The quantity ID cannot be empty or whitespace.", nameof(id));

        Id = id;
    }

    /// <summary>
    ///     Gets the stable ID of the catalog quantity of the elements.
    /// </summary>
    public string Id { get; }
}