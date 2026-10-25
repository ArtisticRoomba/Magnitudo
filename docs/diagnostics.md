# Diagnostics

Magnitudo reports five diagnostics. All are errors in the `DimensionalAnalysis` category, and all are on by default.
Messages print quantities as exponent vectors, as described in
[Gotchas](gotchas.md#messages-show-exponents-not-alias-names).

| ID | Title | Reported when |
| --- | --- | --- |
| [QNT000](#qnt000) | Invalid units catalog | A `.magnitudo` line cannot be parsed or is invalid. |
| [QNT001](#qnt001) | Incompatible quantities | `+`, `-`, or a comparison has operands with different dimensions. |
| [QNT002](#qnt002) | Incompatible quantity assignment | An assigned value does not match the target's quantity. |
| [QNT003](#qnt003) | Incompatible argument quantity | An argument does not match the parameter's quantity. |
| [QNT004](#qnt004) | Incompatible return quantity | A returned value does not match the declared return quantity. |
| [QNT005](#qnt005) | Invalid conversion class | A `[UnitConversions]` class is not static partial, or is generic or nested. |

The examples use this catalog:

```text
length      | Metre          | global::System.Single | length=1
time        | Second         | global::System.Single | time=1
mass        | Kilogram       | global::System.Single | mass=1
velocity    | MetrePerSecond | global::System.Single | length=1,time=-1
force       | Newton         | global::System.Single | mass=1,length=1,time=-2
temperature | Kelvin         | global::System.Single | temperature=1
pressure    | Pascal         | global::System.Single | mass=1,length=-1,time=-2
ratio       | Ratio          | global::System.Single | 1
```

## QNT000

`Invalid units catalog: {0}`

The generator reports this on the offending line of a `.magnitudo` file. It skips the line and loads the rest. The
analyzer reads the catalog silently, so the error needs the generator to run.
[Catalog errors](catalog.md#catalog-errors) lists every message.

```text
Units.magnitudo(4,1): error QNT000: Invalid units catalog: Invalid dimension entry 'length=-1.5'.
```

Exponents are integers. Expect follow-on C# errors wherever the skipped alias appears.

Other causes are a native integer storage type, an alias that is a C# keyword, a conversion on a non-floating storage
type, and a conversion scale or offset that is zero or out of range for its storage type.

## QNT001

`Operator '{0}' cannot combine '{1}' and '{2}'`

The analyzer reports this on `+`, `-`, `==`, `!=`, `<`, `<=`, `>`, or `>=` when both operands are known and their
dimensions differ.

```csharp
internal static float Total(Pascal pressure, Kelvin temperature) => pressure + temperature;
```

```text
error QNT001: Operator '+' cannot combine 'length^-1 mass^1 time^-2' and 'temperature'
```

No mechanical fix exists, because the expression is wrong. A sum of pressure and temperature usually means a missing
term or two swapped variables.

A dimensionless `Ratio` is a quantity, so `d + r` fails with `cannot combine 'length' and 'dimensionless'`. Use
`d * r`. A literal takes the other side's quantity, so `distance > 0f` passes.

## QNT002

`Cannot assign '{0}' to '{1}'`

The analyzer reports this on an assignment, an initializer, a compound assignment, or a vector store. The message
names the value first and the target second.

```csharp
Kelvin temperature = pressure;
// error QNT002: Cannot assign 'length^-1 mass^1 time^-2' to 'temperature'

Metre v = d / t;
// error QNT002: Cannot assign 'length^1 time^-1' to 'length'
```

The second case is a formula that produced a velocity. Change the target to `MetrePerSecond`.

`*=` and `/=` need a dimensionless right side, because anything else changes the target's dimension:

```csharp
distance *= time;    // Cannot assign 'length^1 time^1' to 'length'
distance *= 2f;      // fine
```

A known quantity cannot go into an unannotated target:

```csharp
float raw = pressure;
var inferred = pressure;
// error QNT002: Cannot assign 'length^-1 mass^1 time^-2' to 'unannotated scalar'
```

Declare the alias type: `Pascal inferred = pressure;`. Dimensionless values and discards are exempt.

A vector store is an assignment of each lane, and so is each `out` argument of `Vector.Widen`:

```csharp
Vector<Pascal> p = new Vector<float>(pressures);
p.CopyTo(temperatures);
// error QNT002: Cannot assign 'length^-1 mass^1 time^-2' to 'temperature'

Vector.Widen(p, out Vector<double> low, out Vector<double> high);
// error QNT002 on each out argument: Cannot assign 'length^-1 mass^1 time^-2' to 'unannotated scalar'
```

Here `pressures` is a `Pascal[]` and `temperatures` is a `Kelvin[]`. Declare the halves as `Vector<Pascal64>`.

## QNT003

`Argument has quantity '{0}', but parameter '{1}' requires '{2}'`

The analyzer reports this when a parameter has a quantity and the argument does not satisfy it.

```csharp
internal static void SetTarget(Kelvin value) { }

SetTarget(pressure);
// error QNT003: Argument has quantity 'length^-1 mass^1 time^-2', but parameter 'value' requires 'temperature'

SetTarget(raw);
// error QNT003: Argument has quantity 'unannotated scalar', but parameter 'value' requires 'temperature'
```

An unannotated argument fails here even though assignment accepts it.
[How Magnitudo works](how-it-works.md#what-each-position-accepts) gives the reason. Annotate `raw` at its source, or
write `Kelvin t = raw;` before the call. Literals such as `SetTarget(0f)` pass.

For `out`, the caller's variable must accept the parameter's quantity. `Read(out p)` with `Pascal p` and an
`out Kelvin` parameter fails. The message still names the argument first.

`Abs`, `Clamp`, `Max`, `MaxMagnitude`, `Min`, and `MinMagnitude` on `Math` and `MathF` need matching arguments. The
first known argument sets the expected quantity:

```csharp
Math.Max(pressure, temperature);
// error QNT003: Argument has quantity 'temperature', but parameter 'val2' requires 'length^-1 mass^1 time^-2'
```

Generated conversions carry quantities too, so `UnitConversions.ToCelsius(pressure)` fails the same way.

Vector helpers such as `Vector.Min`, `Vector.ConditionalSelect`, and `Vector.LessThan` follow the same rule. The
[helper table](how-it-works.md#vector-helper-methods) lists them. For `FusedMultiplyAdd(a, b, c)`, the addend `c` must
match the product $[a][b]$.

## QNT004

`Returned expression has quantity '{0}', but the method returns '{1}'`

The analyzer reports this on a `return` statement or an expression body. Property getters are checked against the
property. Async methods are checked against the `T` of `Task<T>`. Local functions and lambdas with a declared return
quantity are checked against themselves, not the enclosing method.

Newton's second law gives $[F] = M\,L\,T^{-2}$. Dropping one factor of time:

```csharp
internal static Newton Force(Kilogram mass, Metre distance, Second time) => mass * distance / time;
```

```text
error QNT004: Returned expression has quantity 'length^1 mass^1 time^-1', but the method returns 'length^1 mass^1 time^-2'
```

Divide by `time * time` to fix it.

A method that returns a plain `float` cannot return a quantity, for the same reason as `float x = pressure;`:

```csharp
internal static float Raw(Pascal pressure) => pressure;
// error QNT004: Returned expression has quantity 'length^-1 mass^1 time^-2', but the method returns 'unannotated scalar'
```

A method declared to return `Kelvin` can return a raw `float`.

## QNT005

`Class '{0}' marked [UnitConversions] must be a static partial, non-generic, top-level class`

The generator reports this at the class name when a `[UnitConversions]` class cannot receive conversions. It skips
that class. The check runs only when the catalog has at least one quantity.

```csharp
[Magnitudo.UnitConversions]
public static class Temperatures { }
// error QNT005: Class 'Temperatures' marked [UnitConversions] must be a static partial, non-generic, top-level class
```

Add `partial` so the generator can add the methods:

```csharp
[Magnitudo.UnitConversions]
public static partial class Temperatures { }
```

A generic class needs its type parameters removed. A nested class needs to move to the top level.

## Severity and suppression

These are ordinary Roslyn diagnostics. Magnitudo adds no controls of its own. Use `.editorconfig` to change a rule's
severity for a project or folder:

```ini
[*.cs]
dotnet_diagnostic.QNT003.severity = warning
dotnet_diagnostic.QNT001.severity = none
```

Use a pragma for a single statement:

```csharp
#pragma warning disable QNT001
return pressure + temperature;
#pragma warning restore QNT001
```

Each suppression is a hole in the analysis. A rule that needs suppressing in many places usually points to a missing
catalog quantity or a missing annotation.
