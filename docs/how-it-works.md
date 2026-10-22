# How Magnitudo works

Magnitudo attaches a physical dimension to values that are plain primitives at runtime. The analyzer tracks those
dimensions through expressions and reports a build error where two of them disagree. This page explains the model and
the rules. [Diagnostics](diagnostics.md) shows each error with a fix.

## Dimensions are exponent vectors

Every quantity has a dimension: a vector of integer exponents over named base dimensions. With $L$, $M$, and $T$ for
length, mass, and time:

$$
[\text{velocity}] = L\,T^{-1}, \qquad [\text{force}] = M\,L\,T^{-2}
$$

The catalog writes these as `length=1,time=-1` and `mass=1,length=1,time=-2`. Base names are free text, so a project can
add `tick` or `currency` without changing Magnitudo.

The analyzer compares exponent vectors, never names. `Kelvin` and `Kelvin64` match because both are `temperature=1`.
Torque and energy also match, which [Gotchas](gotchas.md#same-dimension-different-meaning) covers.

## Why aliases erase to primitives

Each catalog line becomes a `global using` alias:

```csharp
global using Kelvin = global::System.Single;
```

In the compiled assembly, `Kelvin` is `System.Single`. A `Kelvin[]` is a `float[]`, arithmetic is float arithmetic, and
nothing is boxed or wrapped. No Magnitudo type exists at runtime, so Native AOT, trimming, and vectorization behave as
they do on any `float`.

A wrapper struct would give the C# compiler the information instead. It would also cost a type per quantity, operator
overloads for every valid product, and friction with every API that takes a `float`. Magnitudo takes the other side of
that trade. The C# compiler cannot tell `Kelvin` from `Pascal`. Only the analyzer can, and only where the code is
annotated.

## Three states

The analyzer gives every expression one of three states:

| State | Source | Shown in messages as |
| --- | --- | --- |
| Known | A declared quantity, or one derived from known operands | The exponents, e.g. `length^1 time^-1`, or `dimensionless` |
| Literal | A numeric literal, `default`, or a discard | `numeric literal` |
| Unknown | Plain `float`, `var`, and anything the analyzer cannot follow | `unannotated scalar` |

A literal adopts the quantity of whatever it meets, so `distance * 2f + 1f` is a length. An unknown value never causes
an error inside an expression: `length + someFloat` passes. Errors come from two known quantities that disagree, or from
unknown values at certain declaration boundaries.

## Where quantities come from

A declaration gets a quantity from its alias type or from an attribute. When both are present, the attribute wins.

```csharp
Pascal pressure;
static Kelvin Mean(Kelvin a, Kelvin b) => (a + b) / 2f;

[Quantity("pressure")] public float Pressure { get; }
[return: Quantity("temperature")] public static float Read();
```

`Metre?` carries the same quantity as `Metre`. A method declared as `Task<Metre>` returns a length. Its `return`
statements are checked against `length`, and `await` on a call to it is a length.

For arrays and collections the quantity belongs to the elements. The analyzer reads the array element type or the last
generic type argument:

```csharp
Metre[] heights;                   // heights[0] is a length
List<Metre> path;                  // path[3] is a length
Dictionary<int, Metre> byIndex;    // byIndex[7] is a length
```

An indexer that declares its own quantity, such as `public Metre this[int i]`, takes precedence over the element type.
Across assemblies, use `[ElementQuantity("length")]` because the element alias is erased.

The generated conversion methods, such as `UnitConversions.FromCelsius`, carry `[Quantity]` attributes. Their results
and parameters are therefore known.

## How quantities combine

Products and quotients add and subtract exponents. Sums, differences, and comparisons require equal dimensions:

$$
[a \cdot b] = [a]\,[b], \qquad [a \,/\, b] = [a]\,[b]^{-1}, \qquad [a \pm b] = [a] = [b]
$$

| Expression | Result | Check |
| --- | --- | --- |
| `a * b`, `a / b` | Exponents added or subtracted | None |
| `a + b`, `a - b` | The common dimension | [QNT001](diagnostics.md#qnt001) if they differ |
| `==`, `!=`, `<`, `<=`, `>`, `>=` | Not a quantity | QNT001 if they differ |
| `-a`, `+a`, `(a)`, casts | The operand's quantity | None |
| `c ? a : b`, `a ?? b` | The common dimension, otherwise unknown | None |
| `Abs`, `Clamp`, `Max`, `MaxMagnitude`, `Min`, `MinMagnitude` on `Math` or `MathF` | The common dimension | [QNT003](diagnostics.md#qnt003) if arguments differ |
| `CopySign(x, y)` on `Math` or `MathF` | $[x]$ | None, because `y` only supplies a sign |
| A call with a declared return quantity | That quantity | None |
| Anything else | Unknown | None |

A product is never an error. A wrong formula shows up where its result meets a declaration:

```csharp
internal static Newton Force(Kilogram m, Metre d, Second t) => m * d / t;
// QNT004: Returned expression has quantity 'length^1 mass^1 time^-1',
//         but the method returns 'length^1 mass^1 time^-2'
```

A dimensionless alias such as `Ratio` is a known quantity, not a literal. Multiplying by it keeps the other operand's
dimension. Adding it to a length is an error, as is passing it where a length is required. A computed value such as
`d / d` behaves the same way.

## What each position accepts

Unknown values matter at declarations. The rules differ by position on purpose.

| Position | Target is known | Target is unknown |
| --- | --- | --- |
| Assignment, initializer, `+=`, `-=` | Same dimension, a literal, or unknown | Literal, unknown, or dimensionless |
| `*=`, `/=` | The result must keep the target's dimension. A literal or unknown factor is accepted. | Literal, unknown, or dimensionless factor |
| Argument | Same dimension or a literal. Unknown is an error. | Anything |
| `return` | Same dimension, a literal, or unknown | Literal, unknown, or dimensionless |

An unknown value can be assigned to a quantity, but not passed as one. `Kelvin t = raw;` is how raw data gets its unit,
so the analyzer reads it as a claim by the author. At a call site, a raw `float` is more often a missing annotation, so
`Accept(raw)` is [QNT003](diagnostics.md#qnt003).

A known quantity cannot become unknown. `float x = pressure;` and `var x = pressure;` would drop the unit, so both are
[QNT002](diagnostics.md#qnt002). Casts are transparent, so `float x = (float)pressure;` is also reported. Discards are
exempt.

`*=` and `/=` compute the new dimension and compare it with the target. Only a dimensionless factor or a literal keeps
the target's dimension, so `distance *= time` is an error and `distance *= 2f` is not.

Compound assignment follows the same rule for an unannotated numeric target. `float total = 0f; total += pressure;`
is [QNT002](diagnostics.md#qnt002) because `total` would drop the unit. `total *= pressure` and `total /= pressure` are
errors for the same reason, and `total *= ratio` is not. String concatenation with `+=` is not checked.

An `out` argument is checked in reverse: the caller's variable must accept the parameter's quantity. A `ref` argument
is checked like a plain argument. An unannotated `ref float` or generic `ref T` accepts anything, so `Swap(ref a, ref b)`
works on two `Metre` locals.

A `return` is checked against the function that contains it. A local function is checked like a method. A lambda is
checked only when it declares a return quantity, as in `Kelvin () => t`. Otherwise its return type comes from a delegate,
where the alias is already erased.
