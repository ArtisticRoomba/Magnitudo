namespace Magnitudo;

/// <summary>
/// Declares the quantity of a numeric field, property, parameter, or return value.
/// </summary>
/// <remarks>
/// <para>
/// The Magnitudo analyzer reads this attribute at build time. The attribute has no runtime effect, and the
/// annotated value stays a plain primitive.
/// </para>
/// <para>
/// A catalog alias such as <c>Kelvin</c> compiles to its primitive type, so the alias does not appear in
/// metadata. The attribute is what carries the quantity across assembly boundaries. When a declaration has both
/// an alias and this attribute, the attribute wins.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [Quantity("pressure")]
/// public float Pressure { get; }
///
/// [return: Quantity("temperature")]
/// public static float ReadSensor() => 293.15f;
/// </code>
/// </example>
[AttributeUsage(
    AttributeTargets.Field |
    AttributeTargets.Property |
    AttributeTargets.Parameter |
    AttributeTargets.ReturnValue)]
public sealed class QuantityAttribute : Attribute
{
    /// <summary>
    ///     Initializes the attribute with the stable ID of a catalog quantity.
    /// </summary>
    /// <param name="id">The quantity ID, as written in the first column of a <c>*.magnitudo</c> catalog line.</param>
    /// <exception cref="ArgumentNullException"><paramref name="id" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="id" /> is empty or whitespace.</exception>
    public QuantityAttribute(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id;
    }

    /// <summary>
    ///     Gets the stable ID of the catalog quantity.
    /// </summary>
    public string Id { get; }
}