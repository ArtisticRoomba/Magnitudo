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

SIMD vectors are the exception. A `Vector<Metre>` is itself a length, as
[SIMD vectors](#simd-vectors-carry-the-quantity-of-their-lanes) explains.

The generated conversion methods, such as `FromCelsius`, carry `[Quantity]` attributes. Their results and parameters
are therefore known.

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

## SIMD vectors carry the quantity of their lanes

Vector arithmetic works lane by lane. `a + b` on two `Vector<float>` values is one addition per lane, so the whole
vector is the operand. The analyzer therefore treats a vector like a scalar: a `Vector<Pascal>` is a pressure, and the
rules above apply to it unchanged.

```csharp
static Vector<Pascal> Sum(Vector<Pascal> p, Vector<Kelvin> t) => p + t;
// error QNT001: Operator '+' cannot combine 'length^-1 mass^1 time^-2' and 'temperature'
```

This page calls it the lane quantity. Write the alias as the type argument of `System.Numerics.Vector<T>`, or of
`Vector64<T>`, `Vector128<T>`, `Vector256<T>`, or `Vector512<T>` in `System.Runtime.Intrinsics`. It works for locals,
fields, parameters, properties, and return types. An element of `Vector<Pascal>[]` or `Span<Vector<Pascal>>` is a
pressure vector. Across assemblies, put `[Quantity("pressure")]` on the `Vector<float>` declaration, and
`[ElementQuantity("pressure")]` on a `Vector<float>[]`.

A lane has the vector's quantity, so `p[0]` is a pressure. `Vector<float> raw = p;` is
[QNT002](diagnostics.md#qnt002), as `float raw = pressure;` is.

### Broadcasts, loads, and stores

A broadcast literal is a literal. `new Vector<float>(0.5f)`, `Vector<float>.Zero`, `Vector<float>.One`, and
`new Vector<float>()` adopt the quantity they meet, so `p * new Vector<float>(0.5f) + Vector<float>.One` is a pressure.
A broadcast of a quantity keeps it: `new Vector<float>(pressure)` is a pressure vector.

A load takes the element quantity of its source. These forms keep it:

- `new Vector<float>(values)`, `new Vector<float>(values, index)`, and `Vector128.Create(values)` with a typed array or
  span.
- `Vector.LoadUnsafe(ref values[i])` and `Vector.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(values), offset)`.
  The analyzer follows `MemoryMarshal.GetArrayDataReference`, `MemoryMarshal.GetReference`, and `Unsafe.Add`.
- `MemoryMarshal.Cast<Pascal, Vector<Pascal>>(values)[i]`.

The alias in `new Vector<Pascal>(values)` has no effect, like the alias in a cast. The quantity comes from `values`.

A store assigns each lane to an element. `p.CopyTo(destination)`, `Vector.StoreUnsafe(p, ref destination[i])`, and the
other stores follow the assignment rules. A `Kelvin[]` destination for a pressure vector is QNT002. So is a plain
`float[]`, because `raw[i] = pressure` is QNT002 too.

### Vector helper methods

Operators on vectors use the rules for scalars. The methods of `System.Numerics.Vector` and of the `Vector64` to
`Vector512` classes follow this table:

| Methods | Result | Check |
| --- | --- | --- |
| `Abs`, `Negate`, `Add`, `Subtract`, `Min`, `Max` and their `Number`, `Native`, and `Magnitude` forms, `Clamp`, `Round`, `Floor`, `Ceiling`, `Truncate` | The common quantity | [QNT003](diagnostics.md#qnt003) if operands differ |
| `ConditionalSelect(condition, left, right)` | The common quantity of `left` and `right` | QNT003 if they differ. The condition is not checked. |
| `Create`, `CreateScalar`, `Load`, `LoadUnsafe`, `LoadAligned` | The scalar or element quantity | QNT003 if `Create` arguments differ |
| `GetElement`, `ToScalar`, `WithElement`, `GetLower`, `GetUpper`, `WithLower`, `WithUpper`, `Sum` | The lane quantity | None |
| `Narrow`, `WidenLower`, `WidenUpper`, `ConvertToSingle`, `ConvertToDouble`, `ConvertToInt32`, `ConvertToInt64`, and the same-lane `AsVector`, `AsVector64` to `AsVector512` | The lane quantity | None, because these change width or storage type like a cast |
| `Widen(source, out low, out high)` | None | QNT002 if `low` or `high` does not accept the quantity of `source` |
| `Multiply`, `Dot` | The product | None |
| `Divide` | The quotient | None |
| `FusedMultiplyAdd(a, b, c)`, `MultiplyAddEstimate` | $[a][b]$ | QNT003 if $[c] \ne [a][b]$ |
| `CopySign(value, sign)` | $[\text{value}]$ | None, because `sign` only supplies a sign |
| `Equals`, `LessThan`, `LessThanOrEqual`, `GreaterThan`, `GreaterThanOrEqual`, and their `All` and `Any` forms | Unknown | QNT003 if operands differ |
| `BitwiseAnd`, and the `&` operator | The quantity of the known operand | None |
| `AndNot(value, mask)` | $[\text{value}]$ | None |
| `Store`, `StoreUnsafe`, `StoreAligned`, `StoreAlignedNonTemporal`, `CopyTo`, `TryCopyTo` | None | QNT002 if the destination does not accept the vector |
| Anything else, including `SquareRoot`, bit casts such as `AsVectorSingle`, `BitwiseOr`, `Xor`, and shifts | Unknown | None |

The first known operand sets the expected quantity, as for `Math.Max`. The analyzer finds operands by parameter type
and position, not by name. An `int` index or an offset is never an operand.

Masks have no quantity. A comparison returns lanes with all bits set or clear, and nothing about that is dimensional.
`value & mask` is the one bitwise operation that keeps a quantity. Each lane is then the value or zero, so the result
has the value's quantity. Kernels use it to clear lanes before a store:

```csharp
Vector<float> keep = Vector.AsVectorSingle(~bothVacuum);
Vector<KilogramPerSquareMetreSecond> cleared = Vector.BitwiseAnd(mass, keep);
```

`Vector2`, `Vector3`, and `Vector4` have no type argument, so they are not lane vectors. A `[Quantity]` attribute or a
catalog line with `Vector3` storage gives the whole value a quantity, but `.X` has none. Hardware intrinsic classes
such as `Avx`, `Sse`, `AdvSimd`, and `Fma` are not covered. Their results are unknown.
