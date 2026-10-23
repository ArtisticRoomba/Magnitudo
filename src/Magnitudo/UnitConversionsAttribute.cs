namespace Magnitudo;

/// <summary>
/// Marks a class as the home of the catalog's generated unit conversions.
/// </summary>
/// <remarks>
/// <para>
/// The Magnitudo generator adds a <c>From&lt;Unit&gt;</c> and a <c>To&lt;Unit&gt;</c> method to the class for each
/// <c>convert</c> line in the catalog. The generated part has no accessibility modifier, so the modifier you write
/// on your own declaration applies. A public class exposes the conversions to other assemblies.
/// </para>
/// <para>
/// The class must be <see langword="static" />, <see langword="partial" />, non-generic, and not nested. Otherwise
/// the generator reports QNT005 and the generator skips the class.
/// </para>
/// <para>
/// Once any class has this attribute, the generator no longer emits its default internal conversion class.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [UnitConversions]
/// public static partial class Temperatures { }
///
/// Kelvin ambient = Temperatures.FromCelsius(20f);
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class UnitConversionsAttribute : Attribute
{
    /// <summary>
    ///     Initializes the attribute.
    /// </summary>
    public UnitConversionsAttribute()
    {
    }
}
