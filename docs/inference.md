# How the analyzer reasons

The analyzer walks every expression in your code and assigns it one of three states. Everything else in this document
follows from those states and the rules for combining them.

| State | Meaning | Shown in messages as |
| --- | --- | --- |
| Known | The expression has a declared quantity, or one derived from known operands. | The exponents, e.g. `length^1 time^-1`, or `dimensionless` |
| Literal | A numeric literal, `default`, or a discard. It adopts whatever it's combined with. | `numeric literal` |
| Unknown | Nothing is declared. Plain `float`, `var`, and anything the analyzer can't follow. | `unannotated scalar` |

A diagnostic fires when two known quantities with different dimensions meet, with a few extra rules for unknown values
at declaration boundaries. Unknown by itself is never an error in an expression: `length + someFloat` passes.

## Where quantities come from

A quantity is attached to a declaration, either by writing a generated alias as its type or by an attribute.

```csharp
Pascal pressure;                              // alias on a local, field or property
static Kelvin Mean(Kelvin a, Kelvin b) => ...  // alias on a parameter and on a return type

[Quantity("pressure")] public float Pressure { get; }     // attribute
[return: Quantity("temperature")] public static float Read();
```

If a declaration has both, the attribute wins.

Aliases are erased from compiled metadata. A `Pascal` parameter in a library is a plain `float` parameter to a project
that references it, and the analyzer in that project can't see the alias. Public API that other assemblies call should
carry `[Quantity]` so the information survives. Inside one assembly the alias is enough.

### Collections

For arrays and indexable containers, the quantity belongs to the elements, so the type carries it:

```csharp
Metre[] heights;                       // array: element type
List<Metre> path;                      // generic: last type argument
Dictionary<int, Metre> byIndex;        // generic: last type argument
```

`heights[0]`, `path[3]` and `byIndex[7]` are all `length`. Across assemblies the element alias is erased like any
other, so a public array or indexable member should carry `[ElementQuantity("length")]`.

### Generated conversions

The methods in `UnitConversions`, like `FromCelsius`, carry `[Quantity]` attributes, so their results and parameters
are known. The generated file itself is not analyzed: the analyzer skips generated code.

## How quantities combine

Write $[x]$ for the dimension of $x$. The analyzer applies:

$$
[a \cdot b] = [a]\,[b], \qquad [a \,/\, b] = [a]\,[b]^{-1}, \qquad [a \pm b] = [a] = [b]
$$

The last one is a constraint rather than a result: adding or subtracting requires equal dimensions, and the result has
that dimension.

| Expression | Result | Checked |
| --- | --- | --- |
| `a * b` | $[a][b]$ | No constraint |
| `a / b` | $[a][b]^{-1}$ | No constraint |
| `a + b`, `a - b` | The common dimension | [QNT001](diagnostics.md#qnt001) if they differ |
| `==`, `!=`, `<`, `<=`, `>`, `>=` | Not a quantity | QNT001 if the operands differ |
| `-a`, `+a` | $[a]$ | |
| `(a)`, `(float)a`, `(Kelvin)x` | Same as the operand | |
| `c ? a : b`, `a ?? b` | The common dimension, otherwise unknown | |
| `Math.Abs`, `Clamp`, `CopySign`, `Max`, `MaxMagnitude`, `Min`, `MinMagnitude` (also on `MathF`) | The common dimension of the arguments | QNT003 if arguments differ |
| Call to a method with a declared return quantity | That quantity | |
| Anything else | Unknown | |

Two consequences worth stating. A product of two known quantities is always allowed, and its dimension is whatever the
exponents add up to. A bad formula therefore shows up where the result meets a declaration, not at the multiplication:

```csharp
internal static Newton Force(Kilogram m, Metre d, Second t) => m * d / t;
// QNT004: Returned expression has quantity 'length^1 mass^1 time^-1',
//            but the method returns 'length^1 mass^1 time^-2'
```

And `x * x` is not an error either; it's `length^2`:

```csharp
internal static Metre Wrong(Metre d) => d * d;
// QNT004: Returned expression has quantity 'length^2', but the method returns 'length'
```

### Literals and dimensionless values

A numeric literal takes on the quantity of whatever it meets, so ordinary constants work without ceremony:

```csharp
Metre zero = 0f;
Metre next = d * 2f + 1f;       // 1f is read as a length here
```

A dimensionless alias such as `Ratio` is known, and multiplying by it leaves the other operand's dimension unchanged.
It does not get the literal's free pass in sums or assignments:

```csharp
internal static Metre Scale(Metre d, Ratio r) => d * r;    // fine
internal static Metre Shift(Metre d, Ratio r) => d + r;
// QNT001: Operator '+' cannot combine 'length' and 'dimensionless'
```

## What a declaration accepts

Declarations are where unknown values matter. The rules differ by position, and the differences are deliberate.

| Position | Target known | Target unknown |
| --- | --- | --- |
| Assignment, initializer, `+=`, `-=` | Value must be the same dimension, or unknown, or literal. | A known value with units is an error. Literals and unknown are fine. |
| Argument to a parameter | Value must be the same dimension or a literal. **Unknown is an error.** | Anything is accepted. |
| `return` | Value must be the same dimension, or unknown, or literal. | A known value with units is an error. |

Two asymmetries are worth understanding:

**An unannotated value can be assigned to a quantity, but not passed as one.** `Kelvin t = raw;` is accepted, since
that's how a value gets its unit in the first place. `Accept(raw)` where `Accept` takes a `Kelvin` is
[QNT003](diagnostics.md#qnt003). The assignment is a deliberate "this is a temperature" claim by the author; a
call site is more likely to be a mistake or a missing annotation.

**A quantity can't silently become an unannotated value.** Writing `float x = pressure;` or `var x = pressure;` would
lose the unit, so the analyzer rejects it and asks you to name the type you mean:

```csharp
float x = pressure;
// QNT002: Cannot assign 'length^-1 mass^1 time^-2' to 'unannotated scalar'

var y = pressure;
// QNT002: Cannot assign 'length^-1 mass^1 time^-2' to 'unannotated scalar'

Pascal z = pressure;   // fine
```

This applies to `var` because the analyzer reads the declared type syntax and `var` isn't an alias. A cast doesn't get
around it: casts are transparent to the analyzer, so `float x = (float)pressure;` is reported the same way. The ways to
hand a value to unannotated code are to pass it straight to an unannotated parameter, which accepts anything, or to keep
it in a typed local until then.

### Compound assignment

`+=` and `-=` require the right side to match the target. `*=` and `/=` compute the new dimension and compare it with
the target's, so only dimensionless factors are allowed:

```csharp
distance += time;       // QNT002: Cannot assign 'time' to 'length'
distance *= time;       // QNT002: Cannot assign 'length^1 time^1' to 'length'
distance *= 2f;         // fine
distance *= ratio;      // fine
```

Compound assignments are checked only when both sides are known.

### `ref` and `out`

For `out` parameters the value flows from the method to the caller, so the check runs in the other direction: the
argument's variable has to accept what the parameter declares. For `ref`, the value flows both ways and must be
compatible in both. Discards (`out _`) are skipped.

## What the analyzer doesn't check

The analyzer is deliberately permissive where it can't see. These cases produce no diagnostic, and are the usual
reasons a mistake gets through.

- **Unannotated code.** A method with `float` parameters and a `float` return is unknown in and out. Calling it from
  checked code is fine; what comes back has no quantity.
- **`var` locals** have no quantity. They also can't be initialized from a quantity (see above), so in practice this
  only affects `var` initialized from unknown values.
- **Functions that change dimension.** `Math.Sqrt`, `Math.Pow`, `Math.Cbrt` and the rest of `System.Math` outside the
  list above return unknown. `Math.Sqrt(area)` is not recognized as a length.
- **Mismatched branches.** `flag ? length : time` is unknown rather than an error. Using the result in a checked
  position then passes, because unknown is accepted at known targets.
- **The `%` operator**, bit operations, shifts and `++`/`--`.
- **Tuples, deconstruction, and `foreach` variables** have no quantity, even with an alias written as the type.
- **Generated code.** Files Roslyn recognizes as generated, such as `*.g.cs` or those marked `<auto-generated/>`, are
  skipped.
- **Aliases across assemblies,** unless the declaration carries `[Quantity]` or `[ElementQuantity]`.

### Same dimension, different meaning

Quantities are compared by exponent vector alone. Torque and energy are both $M\,L^2\,T^{-2}$, frequency and
radioactivity are both $T^{-1}$, and Quantitas can't tell them apart:

```text
energy | Joule      | global::System.Single | mass=1,length=2,time=-2
torque | NewtonMetre | global::System.Single | mass=1,length=2,time=-2
```

```csharp
Joule e = someTorque;    // no diagnostic
```

The only workaround is to add a base dimension that distinguishes the two, which then has to be carried through every
quantity derived from them. Most projects shouldn't.

### Known quirk: discards

Assigning a quantity to a discard is reported, with an odd message, because the analyzer treats `_` as a literal:

```csharp
_ = pressure;
// QNT002: Cannot assign 'length^-1 mass^1 time^-2' to 'numeric literal'
```

If you need to mark a value as used, assign it to a typed local instead.
