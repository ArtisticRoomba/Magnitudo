# Diagnostics

Magnitudo reports five diagnostics. All are errors, in the category `DimensionalAnalysis`, and all are on by default.

| Id | Title | Reported when |
| --- | --- | --- |
| [`QNT000`](#qnt000) | Invalid units catalog | A `.magnitudo` file can't be parsed or defines an invalid quantity or conversion. |
| [`QNT001`](#qnt001) | Incompatible quantities | `+`, `-` or a comparison combines two operands with different dimensions. |
| [`QNT002`](#qnt002) | Incompatible quantity assignment | A value is assigned to a target with a different quantity. |
| [`QNT003`](#qnt003) | Incompatible argument quantity | An argument's quantity doesn't match the parameter's. |
| [`QNT004`](#qnt004) | Incompatible return quantity | A returned value's quantity doesn't match the method's. |

The examples below use this catalog:

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

## Reading a message

Quantities in messages are printed as their exponent vectors, not their alias names. The analyzer compares
dimensions, so the message shows the dimension it computed.

| Quantity | Printed as |
| --- | --- |
| `Kelvin` | `temperature` |
| `Metre` | `length` |
| `Pascal` | `length^-1 mass^1 time^-2` |
| `Newton` | `length^1 mass^1 time^-2` |
| `Ratio` | `dimensionless` |
| a numeric literal | `numeric literal` |
| an unannotated `float` | `unannotated scalar` |

Base dimensions are sorted alphabetically. An exponent of 1 is omitted only when the quantity has a single base
dimension, so `temperature` has no `^1` but `length^1 time^-1` does. If the printed vector isn't what you expected, the
quickest check is to work it out by hand: $[\text{Pascal}] = M\,L^{-1}\,T^{-2}$ prints as
`length^-1 mass^1 time^-2`.

---

## QNT001

**Incompatible quantities.** `Operator '{0}' cannot combine '{1}' and '{2}'`

Reported on a binary expression with `+`, `-`, `==`, `!=`, `<`, `<=`, `>`, or `>=` when both operands are known and
their dimensions differ. The mathematical rule is $[a] = [b]$ for $a \pm b$ and for any comparison between $a$ and $b$.

### Adding unlike quantities

```csharp
internal static float Total(Pascal pressure, Kelvin temperature)
{
    return pressure + temperature;
}
```

```text
error QNT001: Operator '+' cannot combine 'length^-1 mass^1 time^-2' and 'temperature'
```

There is no mechanical fix; the expression is wrong. Pressure and temperature are related through the ideal gas law,
$p = nRT / V$, so a sum of the two usually means a term is missing, or two variables were swapped.

### Comparing unlike quantities

Comparisons are checked the same way as sums:

```csharp
internal static bool Reached(Metre distance, Second elapsed)
{
    return distance == elapsed;
}
```

```text
error QNT001: Operator '==' cannot combine 'length' and 'time'
```

A comparison against a literal is fine, since the literal takes the other side's quantity: `distance > 0f`.

### Mixing in a dimensionless value

A `Ratio` isn't a literal. It has no dimensions, but it is a declared quantity, and adding it to a length is as wrong as
adding a time:

```csharp
internal static Metre Shift(Metre d, Ratio r) => d + r;
```

```text
error QNT001: Operator '+' cannot combine 'length' and 'dimensionless'
```

Multiplying is allowed: `d * r` is a `length`.

### Elements of arrays and collections

Element access is typed through the container's declared element type, so the same check applies:

```csharp
private static readonly Metre[] Heights = new Metre[4];
private static Second _elapsed;

internal static float Bad() => Heights[0] + _elapsed;
```

```text
error QNT001: Operator '+' cannot combine 'length' and 'time'
```

---

## QNT002

**Incompatible quantity assignment.** `Cannot assign '{0}' to '{1}'`

Reported on an assignment, a variable initializer, or a compound assignment. The message reads *value* first, *target*
second.

### Assigning to a variable of another quantity

```csharp
internal static void Update(Pascal pressure)
{
    Kelvin temperature = pressure;
}
```

```text
error QNT002: Cannot assign 'length^-1 mass^1 time^-2' to 'temperature'
```

Fields, properties, array elements and list elements behave the same way:

```csharp
internal static Kelvin Current;
internal static void Set(Pascal pressure) => Current = pressure;
// Cannot assign 'length^-1 mass^1 time^-2' to 'temperature'

internal static void Fill(Metre[] xs, Second t) => xs[0] = t;
// Cannot assign 'time' to 'length'
```

### Compound assignment

`+=` and `-=` need matching dimensions. `*=` and `/=` need a dimensionless right side, because anything else changes the
target's own dimension:

```csharp
internal static void Step(Metre distance, Second time)
{
    distance += time;     // Cannot assign 'time' to 'length'
    distance *= time;     // Cannot assign 'length^1 time^1' to 'length'
    distance *= 2f;       // fine
}
```

### Losing the unit

A known quantity can't be assigned to an unannotated target. The analyzer treats this as a likely mistake, since the
value has stopped being checked:

```csharp
internal static void Report(Pascal pressure)
{
    float raw = pressure;
    var inferred = pressure;
}
```

```text
error QNT002: Cannot assign 'length^-1 mass^1 time^-2' to 'unannotated scalar'
error QNT002: Cannot assign 'length^-1 mass^1 time^-2' to 'unannotated scalar'
```

Declare the type with the alias: `Pascal inferred = pressure;`. The converse is allowed. `Kelvin t = 293.15f;` and
`Kelvin t = someRawFloat;` are both accepted, because that's how raw data gets its unit.

### A wrong formula, caught at the declaration

Multiplication and division are never errors on their own, so the mistake in a formula shows up where the result
meets a typed target. Velocity is distance over time:

$$
[v] = L\,T^{-1}
$$

```csharp
internal static void Move(Metre d, Second t)
{
    Metre v = d / t;
}
```

```text
error QNT002: Cannot assign 'length^1 time^-1' to 'length'
```

Change the target to `MetrePerSecond`.

### Discards

`_ = pressure;` is reported, with the message `Cannot assign '...' to 'numeric literal'`. This is a known quirk: the
analyzer reads a discard as a literal. Assign to a typed local instead.

---

## QNT003

**Incompatible argument quantity.** `Argument has quantity '{0}', but parameter '{1}' requires '{2}'`

Reported on an argument when the corresponding parameter has a quantity and the argument doesn't satisfy it.

### Passing the wrong quantity

```csharp
internal static void SetTarget(Kelvin value) { }

internal static void Run(Pascal pressure)
{
    SetTarget(pressure);
}
```

```text
error QNT003: Argument has quantity 'length^-1 mass^1 time^-2', but parameter 'value' requires 'temperature'
```

### Passing an unannotated value

A parameter that requires a quantity rejects an unannotated value. This is stricter than assignment, on purpose: at a
call site, a raw `float` is more likely a missing annotation than a deliberate claim.

```csharp
internal static void Run(float raw)
{
    SetTarget(raw);
}
```

```text
error QNT003: Argument has quantity 'unannotated scalar', but parameter 'value' requires 'temperature'
```

Either annotate `raw` at its source, or state the claim with a typed local: `Kelvin t = raw; SetTarget(t);`.

Literals are exempt, so `SetTarget(293.15f)` and `SetTarget(0f)` pass.

### `ref` and `out`

For `out`, the parameter flows into the argument, so the argument's variable has to accept the parameter's quantity:

```csharp
internal static void Read(out Kelvin value) => value = 0f;

internal static void Run()
{
    Pascal p;
    Read(out p);
}
```

```text
error QNT003: Argument has quantity 'temperature', but parameter 'value' requires 'length^-1 mass^1 time^-2'
```

For `ref`, the value passes both ways and both directions are checked. The message names the argument's quantity and
the parameter's.

### `Math.Max` and friends

`Math` and `MathF` methods `Abs`, `Clamp`, `CopySign`, `Max`, `MaxMagnitude`, `Min` and `MinMagnitude` return the same
quantity as their arguments, so the arguments must agree. The first known argument sets the expectation and later ones
are checked against it:

```csharp
internal static Pascal Pick(Pascal pressure, Kelvin temperature)
{
    return Math.Max(pressure, temperature);
}
```

```text
error QNT003: Argument has quantity 'temperature', but parameter 'val2' requires 'length^-1 mass^1 time^-2'
```

`Math.Max(pressure, 0f)` is fine.

### Across a unit conversion

Generated conversions carry the quantity on their parameter and result, so a conversion can't be pointed at the wrong
quantity either. With `convert | temperature | Celsius | ...` in the catalog:

```csharp
using Magnitudo.Generated;

internal static float Show(Pascal pressure)
{
    return UnitConversions.ToCelsius(pressure);
}
```

```text
error QNT003: Argument has quantity 'length^-1 mass^1 time^-2', but parameter 'value' requires 'temperature'
```

---

## QNT004

**Incompatible return quantity.** `Returned expression has quantity '{0}', but the method returns '{1}'`

Reported on a `return` statement or an expression-bodied member whose value doesn't match the declared return quantity.
Property getters are checked against the property's declared quantity.

### Wrong quantity

```csharp
internal static Kelvin Convert(Pascal pressure)
{
    return pressure;
}
```

```text
error QNT004: Returned expression has quantity 'length^-1 mass^1 time^-2', but the method returns 'temperature'
```

### A formula that doesn't balance

The method returns a force. Newton's second law gives

$$
F = m a, \qquad [F] = M\,L\,T^{-2}
$$

and an acceleration is a length over time squared. Dropping a factor of time:

```csharp
internal static Newton Force(Kilogram mass, Metre distance, Second time)
{
    return mass * distance / time;
}
```

```text
error QNT004: Returned expression has quantity 'length^1 mass^1 time^-1', but the method returns 'length^1 mass^1 time^-2'
```

The correct version divides by $t^2$:

```csharp
return mass * distance / (time * time);
```

The analyzer reads `time * time` as `time^2`, then the full expression as `length^1 mass^1 time^-2`, which matches.

### Squaring when you meant to scale

```csharp
internal static Metre Wrong(Metre d) => d * d;
```

```text
error QNT004: Returned expression has quantity 'length^2', but the method returns 'length'
```

### Unannotated return type

A method that returns an unannotated `float` can't return a quantity, for the same reason `float x = pressure;` is
rejected:

```csharp
internal static float Raw(Pascal pressure) => pressure;
```

```text
error QNT004: Returned expression has quantity 'length^-1 mass^1 time^-2', but the method returns 'unannotated scalar'
```

The reverse is allowed: a method declared to return `Kelvin` may return a raw `float`.

### Conversions

A generated conversion returns its canonical quantity:

```csharp
internal static Kelvin Valid() => UnitConversions.FromCelsius(20f);
internal static Pascal Invalid() => UnitConversions.FromCelsius(20f);
```

```text
error QNT004: Returned expression has quantity 'temperature', but the method returns 'length^-1 mass^1 time^-2'
```

---

## QNT000

**Invalid units catalog.** `Invalid units catalog: {0}`

Reported on the offending line of a `.magnitudo` file when a quantity or conversion can't be parsed. The invalid line is
skipped and the others still load. Messages and causes are in [catalog.md](catalog.md#catalog-errors); a few common
ones:

```text
pressure | Pascal | global::System.Single
```

```text
Units.magnitudo(3,1): error QNT000: Invalid units catalog: Expected 'id | alias | storage type | dimensions'.
```

```text
pressure | Pascal | global::System.Single | mass=1,length=-1.5
```

```text
Units.magnitudo(4,1): error QNT000: Invalid units catalog: Invalid dimension entry 'length=-1.5'.
```

```text
convert | temperature | Celsius | global::System.Single | 0 | 273.15
```

```text
Units.magnitudo(5,1): error QNT000: Invalid units catalog: Conversion scale must be a finite, nonzero number.
```

Exponents are whole numbers. Magnitudo doesn't represent fractional exponents.

After a catalog error, expect follow-on errors where the skipped alias is used: the type name is no longer defined, and
the C# compiler will say so.

---

## Severity and suppression

These are ordinary Roslyn diagnostics, so the usual controls apply. Magnitudo adds none of its own.

To change a rule's severity for a project or folder, use `.editorconfig`. This is useful while migrating existing code:

```ini
[*.cs]
dotnet_diagnostic.QNT003.severity = warning
dotnet_diagnostic.QNT001.severity = none
```

With that, `QNT003` is reported as a warning and `QNT001` is not reported at all. If your build uses
`-warnaserror`, as Magnitudo's own CI does, a downgraded rule fails the build again.

A single expression can be suppressed with a pragma:

```csharp
#pragma warning disable QNT001
return pressure + temperature;
#pragma warning restore QNT001
```

A suppression is a hole in the analysis. If the same rule needs
suppressing in many places, the catalog is probably missing a quantity or the code is missing an annotation.

## Build output

In a build, a diagnostic is printed with the usual compiler layout. The location is the expression that is wrong, not
the whole statement:

```text
Physics.cs(7,16): error QNT001: Operator '+' cannot combine 'length^-1 mass^1 time^-2' and 'temperature'
Physics.cs(19,30): error QNT002: Cannot assign 'length^-1 mass^1 time^-2' to 'temperature'
```
