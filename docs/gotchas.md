# Gotchas

These behaviors are by design, but they surprise people. For constructs the analyzer skips entirely, see
[What the analyzer does not check](missing.md).

## Same dimension, different meaning

The analyzer compares exponent vectors only. Torque and energy are both $M\,L^2\,T^{-2}$. Frequency and radioactivity
are both $T^{-1}$.

```text
energy | Joule       | global::System.Single | mass=1,length=2,time=-2
torque | NewtonMetre | global::System.Single | mass=1,length=2,time=-2
```

```csharp
Joule e = someTorque;    // no diagnostic
```

The only workaround is an extra base dimension that separates the two. Every quantity derived from them then has to
carry it. Most projects should accept the overlap.

## Messages show exponents, not alias names

The analyzer compares dimensions, so messages print the dimension it computed. `Pascal` appears as
`length^-1 mass^1 time^-2`. Base names are sorted alphabetically. The `^1` is omitted only for a single base dimension,
so `temperature` has none but `length^1 time^-1` does.

If a printed vector looks wrong, work it out by hand. $[\text{Pascal}] = M\,L^{-1}\,T^{-2}$ prints as
`length^-1 mass^1 time^-2`.

## A wrong formula fails at the declaration

Multiplication and division never fail on their own. `d * d` is a valid `length^2`. The error appears where the result
meets a typed target, which can be some distance from the real mistake. Read the computed exponents in the message
and compare them term by term with the formula.

## `var` cannot hold a quantity

`var x = pressure;` is [QNT002](diagnostics.md#qnt002). The analyzer reads the declared type syntax, and `var` is not
an alias, so the local would be unknown and the unit would be lost. Write `Pascal x = pressure;`.

## Raw values assign freely but do not pass

`Kelvin t = raw;` is accepted. `SetTarget(raw)` with a `Kelvin` parameter is [QNT003](diagnostics.md#qnt003). The
assignment states the unit on purpose. The call site more likely forgot an annotation. To pass a raw value, give it a
unit in a typed local first.

## Conversions are affine

`FromCelsius(a) - FromCelsius(b)` is a difference in kelvin, which is correct. `FromCelsius(a) + FromCelsius(b)` adds two
absolute temperatures. That sum has no physical meaning, and the analyzer cannot detect it. Convert at the edges and do
arithmetic on canonical values.

## One catalog error causes many C# errors

A skipped catalog line removes its alias. Every use of that alias then fails as an undefined type. Fix the
[QNT000](diagnostics.md#qnt000) first. The rest usually disappear.

## Aliases do not cross assemblies

A `Pascal` parameter in a library is a plain `float` to its callers. The calling project's analyzer sees an
unannotated parameter, which accepts anything. An alias-typed field fails the other way. Assigning a `Pascal` to a
library's `Kelvin` field reports [QNT002](diagnostics.md#qnt002) against `unannotated scalar`. Put `[Quantity]` or
`[ElementQuantity]` on public API.

## A hoisted vector constant is not a literal

`new Vector<float>(0.5f)` is a literal, but a local that holds it is not:

```csharp
Vector<float> half = new Vector<float>(0.5f);
Vector<Pascal> mean = half * (a + b);   // half is unknown, so the product is unknown and unchecked
```

The assignment to `mean` passes because an unknown value can claim a unit. The analyzer has stopped checking that
expression without a message. Declare the factor with a dimensionless alias, `Vector<Ratio> half`, or write the
broadcast inline. A hoisted `Vector<float> zero` does no harm in comparisons and in `ConditionalSelect`, because those
take their quantity from the other operand.

## Shared scratch buffers reject quantity vectors

A store into a plain `float[]` or `Span<float>` is [QNT002](diagnostics.md#qnt002), as `raw[i] = pressure` is. Vector
kernels often spill several quantities through one scratch span. Give each quantity its own typed span, such as
`Span<Pascal>`, or suppress QNT002 around the spill.

## `Widen` checks both halves

`Vector.Widen(p, out low, out high)` assigns the lanes of `p` to `low` and `high`. Both must accept the quantity of
`p`, so `out Vector<Pascal64> low` passes and `out Vector<double> low` is QNT002. Narrowing goes through
`Vector.Narrow`, which keeps the quantity.

## Downgraded rules fail again under `-warnaserror`

Setting a rule to `warning` in `.editorconfig` does not help a build that uses `-warnaserror`. Set it to `none` or
`suggestion` instead. Better still, fix the code.
