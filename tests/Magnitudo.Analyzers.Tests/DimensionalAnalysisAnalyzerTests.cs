using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Magnitudo.Analyzers.Tests;

[TestFixture]
public sealed class DimensionalAnalysisAnalyzerTests
{
    private const string Catalog = """
                                   amount | Mole | global::System.Single | amount=1
                                   length | Metre | global::System.Single | length=1
                                   time | Second | global::System.Single | time=1
                                   ratio | Ratio | global::System.Single | 1
                                   temperature | Kelvin | global::System.Single | temperature=1
                                   pressure | Pascal | global::System.Single | mass=1,length=-1,time=-2
                                   pressurePerMoleKelvin | PascalPerMoleKelvin | global::System.Single | mass=1,length=-1,time=-2,temperature=-1,amount=-1
                                   """;

    [Test]
    public async Task CompatibleDerivedExpressionHasNoDiagnostics()
    {
        const string source = """
                              internal static class Physics
                              {
                                  internal static Pascal Pressure(Mole moles, Kelvin temperature,
                                      PascalPerMoleKelvin factor)
                                  {
                                      Pascal result = moles * temperature * factor;
                                      return result;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task IncompatibleAssignmentIsAnError()
    {
        const string source = """
                              internal static class Physics
                              {
                                  internal static void Invalid(Pascal pressure)
                                  {
                                      Kelvin temperature = pressure;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        var diagnostic = diagnostics.Single(item => item.Id == "QNT002");
        Assert.That(
            diagnostic.GetMessage(),
            Is.EqualTo("Cannot assign 'length^-1 mass^1 time^-2' to 'temperature'"));
    }

    [Test]
    public async Task IncompatibleArgumentAndAdditionAreErrors()
    {
        const string source = """
                              internal static class Physics
                              {
                                  private static void AcceptTemperature(Kelvin value) { }

                                  internal static void Invalid(Pascal pressure, Kelvin temperature)
                                  {
                                      AcceptTemperature(pressure);
                                      _ = pressure + temperature;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.Multiple(() =>
        {
            Assert.That(
                diagnostics.Select(diagnostic => diagnostic.Id),
                Does.Contain("QNT001"));

            Assert.That(
                diagnostics.Select(diagnostic => diagnostic.Id),
                Does.Contain("QNT003"));
        });
    }

    [Test]
    public async Task CatalogCanAddAnIndependentBaseDimension()
    {
        const string source = """
                              internal static class Simulation
                              {
                                  internal static PerTick Scale(PerTick value, float ratio) => value * ratio;
                              }
                              """;

        const string catalog = "perTick | PerTick | global::System.Single | tick=-1";

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, catalog);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task GeneratedConversionCarriesItsCanonicalQuantity()
    {
        const string source = """
                              using Magnitudo.Generated;

                              internal static class Inputs
                              {
                                  internal static Kelvin Valid() => UnitConversions.FromCelsius(20f);
                                  internal static Pascal Invalid() => UnitConversions.FromCelsius(20f);
                              }
                              """;

        string catalog = Catalog +
                         Environment.NewLine +
                         "convert | temperature | Celsius | global::System.Single | 1 | 273.15";

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, catalog);

        Assert.That(
            diagnostics.Select(diagnostic => diagnostic.Id),
            Is.EquivalentTo(new[] { "QNT004" }));
    }

    [Test]
    public void GeneratedAliasRetainsPrimitiveClrType()
    {
        const string source = """
                              internal static class Storage
                              {
                                  internal static Kelvin Value;
                                  internal static Mole[] Values = new Mole[4];
                              }
                              """;

        var compilation = GenerateAliases(CreateCompilation(source), Catalog, out _);
        var storage = compilation.GetTypeByMetadataName("Storage")!;

        Assert.Multiple(() =>
        {
            Assert.That(
                ((IFieldSymbol)storage.GetMembers("Value").Single()).Type.SpecialType,
                Is.EqualTo(SpecialType.System_Single));

            var array = (IArrayTypeSymbol)((IFieldSymbol)storage.GetMembers("Values").Single()).Type;
            Assert.That(array.ElementType.SpecialType, Is.EqualTo(SpecialType.System_Single));
        });
    }

    [Test]
    public async Task LambdaReturnIsNotCheckedAgainstEnclosingMethod()
    {
        const string source = """
                              using System;

                              internal static class Physics
                              {
                                  internal static void Run(Pascal pressure)
                                  {
                                      Func<Pascal> read = () => pressure;
                                  }

                                  internal static Pascal Pick(Pascal pressure, Kelvin temperature)
                                  {
                                      Func<Kelvin> read = () => temperature;
                                      return pressure;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task LambdaWithDeclaredReturnTypeIsChecked()
    {
        const string source = """
                              using System;

                              internal static class Physics
                              {
                                  internal static void Run(Pascal pressure)
                                  {
                                      Func<Kelvin> read = Kelvin () => pressure;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(
            diagnostics.Single().GetMessage(),
            Is.EqualTo(
                "Returned expression has quantity 'length^-1 mass^1 time^-2', but the method returns 'temperature'"));
    }

    [Test]
    public async Task LocalFunctionReturnIsCheckedAgainstLocalFunction()
    {
        const string source = """
                              internal static class Physics
                              {
                                  internal static Pascal Run(Pascal pressure)
                                  {
                                      Kelvin Read() => pressure;
                                      Pascal Echo() => pressure;
                                      return pressure;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(
            diagnostics.Single().GetMessage(),
            Is.EqualTo(
                "Returned expression has quantity 'length^-1 mass^1 time^-2', but the method returns 'temperature'"));
    }

    [Test]
    public async Task AsyncMethodReturnsTaskResultQuantity()
    {
        const string source = """
                              using System.Threading.Tasks;

                              internal static class Physics
                              {
                                  private static void Accept(Metre value) { }

                                  internal static async Task<Metre> Valid(Metre distance)
                                  {
                                      await Task.Yield();
                                      return distance;
                                  }

                                  internal static async Task<Metre> Invalid(Second time)
                                  {
                                      await Task.Yield();
                                      return time;
                                  }

                                  internal static async Task Caller() => Accept(await Valid(1f));
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(
            diagnostics.Single().GetMessage(),
            Is.EqualTo("Returned expression has quantity 'time', but the method returns 'length'"));
    }

    [Test]
    public async Task DimensionlessValueIsNotTreatedAsLiteral()
    {
        const string source = """
                              internal static class Physics
                              {
                                  private static void AcceptTemperature(Kelvin value) { }

                                  internal static void Invalid(Ratio ratio, Metre distance)
                                  {
                                      AcceptTemperature(ratio);
                                      AcceptTemperature(distance / distance);
                                  }

                                  internal static void Valid(Ratio ratio, Metre width, Metre height)
                                  {
                                      float raw = ratio;
                                      float aspect = width / height;
                                      AcceptTemperature(0f);
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(
            diagnostics.Select(diagnostic => diagnostic.GetMessage()),
            Is.EqualTo(
                Enumerable.Repeat(
                    "Argument has quantity 'dimensionless', but parameter 'value' requires 'temperature'",
                    2)));
    }

    [Test]
    public async Task StringConcatenationIsNotChecked()
    {
        const string source = """
                              internal static class Physics
                              {
                                  internal static string Describe(Pascal pressure, Kelvin temperature) =>
                                      "p=" + pressure + " T=" + temperature;
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task OutArgumentMessageNamesArgumentThenParameter()
    {
        const string source = """
                              internal static class Physics
                              {
                                  private static void Read(out Kelvin value) => value = 0f;

                                  internal static void Run()
                                  {
                                      Pascal pressure;
                                      Read(out pressure);
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(
            diagnostics.Single().GetMessage(),
            Is.EqualTo(
                "Argument has quantity 'length^-1 mass^1 time^-2', but parameter 'value' requires 'temperature'"));
    }

    [Test]
    public async Task RefArgumentToUnannotatedParameterIsAccepted()
    {
        const string source = """
                              internal static class Physics
                              {
                                  private static void Swap<T>(ref T a, ref T b) => (a, b) = (b, a);
                                  private static void Scale(ref Metre value) => value *= 2f;

                                  internal static void Run(Metre first, Metre second, Second time, float raw)
                                  {
                                      Swap(ref first, ref second);
                                      Scale(ref first);
                                      Scale(ref time);
                                      Scale(ref raw);
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(
            diagnostics.Select(diagnostic => diagnostic.GetMessage()),
            Is.EqualTo(new[]
            {
                "Argument has quantity 'time', but parameter 'value' requires 'length'",
                "Argument has quantity 'unannotated scalar', but parameter 'value' requires 'length'"
            }));
    }

    [Test]
    public async Task DiscardAcceptsAnyQuantity()
    {
        const string source = """
                              internal static class Physics
                              {
                                  private static Pascal Read() => 0f;

                                  internal static void Run(Kelvin temperature)
                                  {
                                      _ = Read();
                                      _ = temperature;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task ElementQuantityComesFromIndexersAndMethodReturns()
    {
        const string source = """
                              using System.Collections.Generic;
                              using Magnitudo;

                              internal sealed class Path
                              {
                                  public Metre this[int index] => 0f;
                              }

                              internal static class Physics
                              {
                                  [return: ElementQuantity("length")]
                                  private static float[] Heights() => new float[1];

                                  private static List<Metre> Points() => new();

                                  internal static void Run(Path path, Second time)
                                  {
                                      Second a = path[0] + time;
                                      Second b = Heights()[0] + time;
                                      Second c = Points()[0] + time;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(
            diagnostics.Select(diagnostic => diagnostic.GetMessage()),
            Is.EqualTo(Enumerable.Repeat("Operator '+' cannot combine 'length' and 'time'", 3)));
    }

    [Test]
    public async Task NullableCollectionKeepsItsElementQuantity()
    {
        const string source = """
                              #nullable enable
                              using System.Collections.Generic;

                              internal static class Physics
                              {
                                  private static Mole[]? Moles;

                                  internal static void Run(Mole[]? values, List<Metre>? points, ref Mole[] moles)
                                  {
                                      Mole ok = values![0];
                                      Metre okMetre = points![0];
                                      Second a = values![0];
                                      Second b = Moles![0];
                                      Second c = points![0];
                                      Second d = moles[0];
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(
            diagnostics.Select(diagnostic => diagnostic.Id),
            Is.EqualTo(Enumerable.Repeat("QNT002", 4)));
    }

    [Test]
    public async Task NullConditionalElementAccessKeepsItsElementQuantity()
    {
        const string source = """
                              #nullable enable
                              internal static class Physics
                              {
                                  internal static void Run(Mole[]? values)
                                  {
                                      Mole ok = values?[0] ?? 0;
                                      Mole? okNullable = values?[0];
                                      Second a = values?[0] ?? 0;
                                      Second? b = values?[0];
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(
            diagnostics.Select(diagnostic => diagnostic.Id),
            Is.EqualTo(Enumerable.Repeat("QNT002", 2)));
    }

    [Test]
    public async Task NullableAliasKeepsItsQuantity()
    {
        const string source = """
                              internal static class Physics
                              {
                                  internal static Second? Invalid(Metre? distance, Second time) => distance + time;
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(
            diagnostics.Single().GetMessage(),
            Is.EqualTo("Operator '+' cannot combine 'length' and 'time'"));
    }

    [Test]
    public async Task CopySignTakesQuantityOfFirstArgument()
    {
        const string source = """
                              using System;

                              internal static class Physics
                              {
                                  internal static Metre Valid(Metre distance, Second time) =>
                                      MathF.CopySign(distance, time);

                                  internal static Second Invalid(Metre distance, Second time) =>
                                      MathF.CopySign(distance, time);
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(
            diagnostics.Single().GetMessage(),
            Is.EqualTo("Returned expression has quantity 'length', but the method returns 'time'"));
    }

    [Test]
    public async Task ConversionWithKeywordStorageTypeCompiles()
    {
        const string source = """
                              using Magnitudo.Generated;

                              internal static class Inputs
                              {
                                  internal static Kelvin Read() => UnitConversions.FromCelsius(20f);
                              }
                              """;

        const string catalog = """
                               temperature | Kelvin | float | temperature=1
                               convert | temperature | Celsius | float | 1 | 273.15
                               """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, catalog);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public void GeneratedAliasesCompileWithCSharp10()
    {
        const string source = """
                              using Magnitudo.Generated;

                              internal static class Inputs
                              {
                                  internal static Kelvin Read(Metre length) => UnitConversions.FromCelsius(20f);
                                  internal static MaybeMetre Maybe = null;
                                  internal static MaybeCount Count = null;
                                  internal static Label Name = "";
                                  internal static Thing Anything = new object();
                                  internal static MaybeLabel Optional = null;
                              }
                              """;

        const string catalog = """
                               length | Metre | global::System.Single | length=1
                               temperature | Kelvin | float | temperature=1
                               maybeLength | MaybeMetre | float? | length=1
                               maybeCount | MaybeCount | System.Int32? | count=1
                               label | Label | string | label=1
                               optionalLabel | MaybeLabel | string? | label=1
                               thing | Thing | object | thing=1
                               convert | temperature | Celsius | float | 1 | 273.15
                               """;

        var compilation = GenerateAliases(
            CreateCompilation(source, languageVersion: LanguageVersion.CSharp10),
            catalog,
            out ImmutableArray<Diagnostic> generatorDiagnostics);

        Assert.That(generatorDiagnostics, Is.Empty);
        Assert.That(
            compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity != DiagnosticSeverity.Hidden),
            Is.Empty);

        string generated = GeneratedSource(compilation);
        Assert.Multiple(() =>
        {
            Assert.That(generated, Does.Contain("global using MaybeMetre = global::System.Nullable<global::System.Single>;"));
            Assert.That(generated, Does.Contain("global using MaybeCount = global::System.Nullable<global::System.Int32>;"));
            Assert.That(generated, Does.Contain("global using Label = global::System.String;"));
            Assert.That(generated, Does.Contain("global using MaybeLabel = global::System.String;"));
            Assert.That(generated, Does.Contain("global using Thing = global::System.Object;"));
        });
    }

    [TestCase("handle | Handle | nint | length=1", "nint")]
    [TestCase("handle | Handle | nuint | length=1", "nuint")]
    [TestCase("handle | Handle | nint? | length=1", "nint?")]
    [TestCase("handle | Handle | System.IntPtr | length=1", "System.IntPtr")]
    [TestCase("handle | Handle | global::System.UIntPtr | length=1", "global::System.UIntPtr")]
    public void NativeIntegerStorageIsRejected(string line, string storage)
    {
        var compilation = GenerateAliases(
            CreateCompilation("internal static class C { }"),
            line,
            out ImmutableArray<Diagnostic> diagnostics);

        Assert.Multiple(() =>
        {
            Assert.That(
                diagnostics.Select(diagnostic => diagnostic.GetMessage()),
                Is.EqualTo(new[]
                {
                    $"Invalid units catalog: Storage type '{storage}' is not supported. Use 'int' or 'long'."
                }));
            Assert.That(GeneratedSource(compilation), Is.Empty);
        });
    }

    [Test]
    public void QuantityIdMayStartWithConvert()
    {
        GenerateAliases(
            CreateCompilation("internal static class C { internal static Converter Value; }"),
            "converter | Converter | global::System.Single | length=1",
            out ImmutableArray<Diagnostic> diagnostics);

        Assert.That(diagnostics, Is.Empty);
    }

    [TestCase(
        "length | Metre | global::System.Single | length=1\nlength | Metre64 | global::System.Double | length=1",
        "Invalid units catalog: Quantity ID 'length' is declared more than once.")]
    [TestCase(
        "length | Metre | global::System.Single | length=1,length=2",
        "Invalid units catalog: Dimension 'length' is listed more than once.")]
    public void CatalogRejectsDuplicates(string catalog, string message)
    {
        GenerateAliases(
            CreateCompilation("internal static class C { }"),
            catalog,
            out ImmutableArray<Diagnostic> diagnostics);

        Assert.That(diagnostics.Select(diagnostic => diagnostic.GetMessage()), Is.EqualTo(new[] { message }));
    }

    private const string MechanicsCatalog = """
                                            mass | Kilogram | global::System.Single | mass=1
                                            length | Metre | global::System.Single | length=1
                                            time | Second | global::System.Single | time=1
                                            velocity | MetrePerSecond | global::System.Single | length=1,time=-1
                                            frequency | Hertz | global::System.Single | time=-1
                                            force | Newton | global::System.Single | mass=1,length=1,time=-2
                                            energy | Joule | global::System.Single | mass=1,length=2,time=-2
                                            torque | NewtonMetre | global::System.Single | mass=1,length=2,time=-2
                                            """;

    private const string IntegerCatalog = """
                                          count | Count | global::System.Int32 | count=1
                                          bytes | Bytes | global::System.Int32 | bytes=1
                                          """;

    [Test]
    public async Task PlainFloatMethodResultHasNoQuantity()
    {
        const string source = """
                              internal static class Physics
                              {
                                  private static float Raw() => 1f;
                                  private static float Scale(float value) => value * 2f;

                                  internal static void Run(Metre distance)
                                  {
                                      Second a = Raw();
                                      Second b = Scale(distance);
                                      Second c = distance + Raw();
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(diagnostics, Is.Empty);
    }

    [TestCase("MathF.Sqrt(distance)")]
    [TestCase("MathF.Pow(distance, 2f)")]
    [TestCase("MathF.Floor(distance)")]
    [TestCase("MathF.Sin(distance)")]
    [TestCase("(float)Math.Sqrt(distance)")]
    [TestCase("(float)Math.Cbrt(distance)")]
    [TestCase("(float)Math.Pow(distance, 2d)")]
    public async Task MathMethodsOutsideTheQuantityPreservingListHaveNoQuantity(string expression)
    {
        string source = $$"""
                          using System;

                          internal static class Physics
                          {
                              internal static void Run(Metre distance)
                              {
                                  Second result = {{expression}};
                              }
                          }
                          """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task MismatchedConditionalAssignedToTypedLocalHasNoDiagnostic()
    {
        const string source = """
                              internal static class Physics
                              {
                                  internal static void Run(bool flag, Metre length, Second time)
                                  {
                                      Metre m = flag ? length : time;
                                      Kelvin k = flag ? length : time;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task MismatchedConditionalPassedToTypedParameterIsAnUnannotatedArgument()
    {
        const string source = """
                              internal static class Physics
                              {
                                  private static void Accept(Metre value) { }

                                  internal static void Run(bool flag, Metre length, Second time)
                                  {
                                      Accept(flag ? length : time);
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(
            diagnostics.Single().GetMessage(),
            Is.EqualTo("Argument has quantity 'unannotated scalar', but parameter 'value' requires 'length'"));
    }

    [Test]
    public async Task ConditionalWithMatchingBranchesKeepsItsQuantity()
    {
        const string source = """
                              internal static class Physics
                              {
                                  internal static void Run(bool flag, Metre a, Metre b)
                                  {
                                      Metre valid = flag ? a : b;
                                      Metre literal = flag ? a : 0f;
                                      Second invalid = flag ? a : b;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(
            diagnostics.Single().GetMessage(),
            Is.EqualTo("Cannot assign 'length' to 'time'"));
    }

    [TestCase("count % bytes")]
    [TestCase("count & bytes")]
    [TestCase("count | bytes")]
    [TestCase("count ^ bytes")]
    [TestCase("count << bytes")]
    [TestCase("count >> bytes")]
    public async Task RemainderBitwiseAndShiftOperatorsProduceUnknown(string expression)
    {
        string source = $$"""
                          internal static class Physics
                          {
                              private static void Accept(Count value) { }

                              internal static void Run(Count count, Bytes bytes)
                              {
                                  Bytes result = {{expression}};
                              }
                          }
                          """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, IntegerCatalog);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task RemainderOfMismatchedQuantitiesHasNoDiagnostic()
    {
        const string source = """
                              internal static class Physics
                              {
                                  internal static void Run(Metre distance, Second time)
                                  {
                                      Kelvin result = distance % time;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task IncrementAndDecrementProduceUnknown()
    {
        const string source = """
                              internal static class Physics
                              {
                                  internal static void Run(Count count)
                                  {
                                      Bytes a = count++;
                                      Bytes b = --count;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, IntegerCatalog);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task VarInitializedFromUnknownCarriesNoQuantity()
    {
        const string source = """
                              internal static class Physics
                              {
                                  private static float Raw() => 1f;

                                  internal static void Run(bool flag, Metre length, Second time)
                                  {
                                      var fromCall = Raw();
                                      var fromConditional = flag ? length : time;
                                      Metre a = fromCall;
                                      Second b = fromCall;
                                      Metre c = fromConditional;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task TuplesAndDeconstructionCarryNoQuantity()
    {
        const string source = """
                              internal static class Physics
                              {
                                  internal static void Run(Metre distance, Second time)
                                  {
                                      (Metre a, Second b) = (time, distance);
                                      (Metre, Second) pair = (time, distance);
                                      Metre x;
                                      Second y;
                                      (x, y) = (time, distance);
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task ForeachVariableIsNotComparedWithTheElementType()
    {
        const string source = """
                              internal static class Physics
                              {
                                  internal static void Run(Second[] times)
                                  {
                                      foreach (Metre m in times) { }
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task WholeArrayAndCollectionAssignmentIsNotChecked()
    {
        const string source = """
                              using System.Collections.Generic;

                              internal static class Physics
                              {
                                  internal static void Run(Pascal[] pascals, List<Pascal> list)
                                  {
                                      Metre[] xs = pascals;
                                      List<Metre> ys = list.ConvertAll(p => (Metre)p);
                                      Metre[] zs;
                                      zs = pascals;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task WholeArrayArgumentIsNotChecked()
    {
        const string source = """
                              internal static class Physics
                              {
                                  private static void Take(Metre[] values) { }

                                  internal static void Run(Pascal[] pascals)
                                  {
                                      Take(pascals);
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task YieldReturnIsNotChecked()
    {
        const string source = """
                              using System.Collections.Generic;

                              internal static class Physics
                              {
                                  internal static IEnumerable<Metre> Generate(Second time)
                                  {
                                      yield return time;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task LambdaWithoutDeclaredReturnQuantityIsNotChecked()
    {
        const string source = """
                              using System;

                              internal static class Physics
                              {
                                  private static void Use(Func<Kelvin> read) { }

                                  internal static void Run(Pascal pressure)
                                  {
                                      Func<Kelvin> read = () => pressure;
                                      Use(() => pressure);
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task GeneratedCodeIsSkipped()
    {
        const string body = """
                            internal static class Physics
                            {
                                private static void Accept(Kelvin value) { }

                                internal static Kelvin Run(Pascal pressure, Kelvin temperature)
                                {
                                    Accept(pressure);
                                    _ = pressure + temperature;
                                    Kelvin t = pressure;
                                    return pressure;
                                }
                            }
                            """;

        ImmutableArray<Diagnostic> generated = await Analyze("// <auto-generated/>\n" + body, Catalog);
        ImmutableArray<Diagnostic> handWritten = await Analyze(body, Catalog);

        Assert.Multiple(() =>
        {
            Assert.That(generated, Is.Empty);
            Assert.That(
                handWritten.Select(diagnostic => diagnostic.Id),
                Is.EquivalentTo(new[] { "QNT001", "QNT002", "QNT003", "QNT004" }));
        });
    }

    [TestCase("Math", "Abs(a)")]
    [TestCase("Math", "Min(a, b)")]
    [TestCase("Math", "Max(a, b)")]
    [TestCase("Math", "Clamp(a, b, b)")]
    [TestCase("MathF", "Abs(a)")]
    [TestCase("MathF", "Min(a, b)")]
    [TestCase("MathF", "Max(a, b)")]
    [TestCase("MathF", "MaxMagnitude(a, b)")]
    [TestCase("MathF", "MinMagnitude(a, b)")]
    public async Task QuantityPreservingMathAcceptsMatchingQuantities(string type, string call)
    {
        string source = $$"""
                          using System;

                          internal static class Physics
                          {
                              internal static Metre Run(Metre a, Metre b) => {{type}}.{{call}};
                          }
                          """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(diagnostics, Is.Empty);
    }

    [TestCase("Math", "Abs(a)")]
    [TestCase("Math", "Min(a, b)")]
    [TestCase("Math", "Max(a, b)")]
    [TestCase("Math", "Clamp(a, b, b)")]
    [TestCase("MathF", "Abs(a)")]
    [TestCase("MathF", "Max(a, b)")]
    [TestCase("MathF", "MinMagnitude(a, b)")]
    public async Task QuantityPreservingMathResultIsCheckedAgainstTheTarget(string type, string call)
    {
        string source = $$"""
                          using System;

                          internal static class Physics
                          {
                              internal static void Run(Metre a, Metre b)
                              {
                                  Second result = {{type}}.{{call}};
                              }
                          }
                          """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(diagnostics.Single().GetMessage(), Is.EqualTo("Cannot assign 'length' to 'time'"));
    }

    [TestCase("Math", "Min")]
    [TestCase("Math", "Max")]
    [TestCase("Math", "MaxMagnitude")]
    [TestCase("Math", "MinMagnitude")]
    [TestCase("MathF", "Min")]
    [TestCase("MathF", "Max")]
    [TestCase("MathF", "MaxMagnitude")]
    [TestCase("MathF", "MinMagnitude")]
    public async Task QuantityPreservingMathReportsMismatchedArguments(string type, string method)
    {
        string source = $$"""
                          using System;

                          internal static class Physics
                          {
                              internal static void Run(Metre distance, Second time)
                              {
                                  _ = {{type}}.{{method}}(distance, time);
                              }
                          }
                          """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        var diagnostic = diagnostics.Single();
        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Id, Is.EqualTo("QNT003"));
            Assert.That(diagnostic.GetMessage(), Does.StartWith("Argument has quantity 'time', but parameter '"));
            Assert.That(diagnostic.GetMessage(), Does.EndWith("' requires 'length'"));
            Assert.That(SpanText(diagnostic), Is.EqualTo("time"));
        });
    }

    [Test]
    public async Task ClampReportsTheArgumentThatDiffersFromTheFirstKnownOne()
    {
        const string source = """
                              using System;

                              internal static class Physics
                              {
                                  internal static void Run(Metre value, Metre low, Second high)
                                  {
                                      _ = Math.Clamp(value, low, high);
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        var diagnostic = diagnostics.Single();
        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Id, Is.EqualTo("QNT003"));
            Assert.That(SpanText(diagnostic), Is.EqualTo("high"));
        });
    }

    [Test]
    public async Task QuantityPreservingMathIgnoresLiteralsAndUnknownArguments()
    {
        const string source = """
                              using System;

                              internal static class Physics
                              {
                                  internal static Metre Run(Metre distance, float raw)
                                  {
                                      _ = Math.Max(distance, 0f);
                                      _ = Math.Max(raw, distance);
                                      return Math.Min(distance, raw);
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task QuantityPreservingMathNestsThroughOtherCalls()
    {
        const string source = """
                              using System;

                              internal static class Physics
                              {
                                  internal static void Run(Metre distance, Second time)
                                  {
                                      _ = Math.Max(Math.Abs(distance), time);
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(
            diagnostics.Select(diagnostic => diagnostic.Id),
            Is.EqualTo(new[] { "QNT003" }));
    }

    [Test]
    public async Task SumOfMismatchedQuantitiesIsReportedOnTheExpression()
    {
        const string source = """
                              internal static class Physics
                              {
                                  internal static void Run(Metre distance, Second time)
                                  {
                                      _ = distance + time;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        var diagnostic = diagnostics.Single();
        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Id, Is.EqualTo("QNT001"));
            Assert.That(diagnostic.GetMessage(), Is.EqualTo("Operator '+' cannot combine 'length' and 'time'"));
            Assert.That(SpanText(diagnostic), Is.EqualTo("distance + time"));
        });
    }

    [Test]
    public async Task DifferenceOfMismatchedQuantitiesIsReported()
    {
        const string source = """
                              internal static class Physics
                              {
                                  internal static void Run(Metre distance, Second time)
                                  {
                                      _ = distance - time;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(
            diagnostics.Single().GetMessage(),
            Is.EqualTo("Operator '-' cannot combine 'length' and 'time'"));
    }

    [Test]
    public async Task SumWithLiteralOrUnannotatedOperandHasNoDiagnostic()
    {
        const string source = """
                              internal static class Physics
                              {
                                  internal static Metre Run(Metre distance, float raw)
                                  {
                                      _ = distance + 1f;
                                      _ = distance - raw;
                                      return distance * 2f + 1f;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(diagnostics, Is.Empty);
    }

    [TestCase("==")]
    [TestCase("!=")]
    [TestCase("<")]
    [TestCase("<=")]
    [TestCase(">")]
    [TestCase(">=")]
    public async Task ComparisonOfMismatchedQuantitiesIsReported(string op)
    {
        string source = $$"""
                          internal static class Physics
                          {
                              internal static bool Run(Metre distance, Second time) => distance {{op}} time;
                          }
                          """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        var diagnostic = diagnostics.Single();
        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Id, Is.EqualTo("QNT001"));
            Assert.That(
                diagnostic.GetMessage(),
                Is.EqualTo($"Operator '{op}' cannot combine 'length' and 'time'"));
        });
    }

    [Test]
    public async Task ComparisonResultIsNotAQuantity()
    {
        const string source = """
                              internal static class Physics
                              {
                                  private static void Accept(Metre value) { }

                                  internal static void Run(Metre a, Metre b)
                                  {
                                      bool less = a < b;
                                      bool positive = a > 0f;
                                      bool same = a == b;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task DimensionlessAliasCannotBeAddedToOrComparedWithLength()
    {
        const string source = """
                              internal static class Physics
                              {
                                  internal static void Run(Metre distance, Ratio ratio)
                                  {
                                      _ = distance + ratio;
                                      _ = distance > ratio;
                                      _ = distance * ratio;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(
            diagnostics.Select(diagnostic => diagnostic.GetMessage()),
            Is.EquivalentTo(new[]
            {
                "Operator '+' cannot combine 'length' and 'dimensionless'",
                "Operator '>' cannot combine 'length' and 'dimensionless'"
            }));
    }

    [Test]
    public async Task ProductAndQuotientMatchingTheTypedTargetHaveNoDiagnostics()
    {
        const string source = """
                              internal static class Physics
                              {
                                  internal static void Run(Pascal pressure, Mole moles, Kelvin temperature)
                                  {
                                      PascalPerMoleKelvin factor = pressure / (moles * temperature);
                                      Pascal back = factor * moles * temperature;
                                      Ratio unit = pressure / pressure;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task ProductAssignedToTheWrongTargetReportsDerivedExponents()
    {
        const string source = """
                              internal static class Physics
                              {
                                  internal static void Run(Mole moles, Kelvin temperature)
                                  {
                                      Pascal p = moles * temperature;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        var diagnostic = diagnostics.Single();
        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Id, Is.EqualTo("QNT002"));
            Assert.That(
                diagnostic.GetMessage(),
                Is.EqualTo("Cannot assign 'amount^1 temperature^1' to 'length^-1 mass^1 time^-2'"));
        });
    }

    [Test]
    public async Task QuotientAssignedToTheWrongTargetReportsDerivedExponents()
    {
        const string source = """
                              internal static class Physics
                              {
                                  internal static void Run(Metre d, Second t)
                                  {
                                      Metre v = d / t;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(
            diagnostics.Single().GetMessage(),
            Is.EqualTo("Cannot assign 'length^1 time^-1' to 'length'"));
    }

    [Test]
    public async Task LiteralNumeratorInvertsTheDenominator()
    {
        const string source = """
                              internal static class Physics
                              {
                                  internal static void Run(Second time)
                                  {
                                      Hertz valid = 1f / time;
                                      Second invalid = 1f / time;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, MechanicsCatalog);

        Assert.That(
            diagnostics.Single().GetMessage(),
            Is.EqualTo("Cannot assign 'time^-1' to 'time'"));
    }

    [Test]
    public async Task UnaryMinusAndPlusKeepTheOperandQuantity()
    {
        const string source = """
                              internal static class Physics
                              {
                                  internal static void Run(Metre distance)
                                  {
                                      Metre a = -distance;
                                      Metre b = +distance;
                                      Second c = -distance;
                                      Second d = +distance;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(
            diagnostics.Select(diagnostic => diagnostic.GetMessage()),
            Is.EqualTo(Enumerable.Repeat("Cannot assign 'length' to 'time'", 2)));
    }

    [Test]
    public async Task CoalesceOfMismatchedQuantitiesIsUnknown()
    {
        const string source = """
                              internal static class Physics
                              {
                                  private static void Accept(Metre value) { }

                                  internal static void Run(Metre? distance, Second? time)
                                  {
                                      Metre assigned = distance ?? time ?? 0f;
                                      Accept(distance ?? time ?? 0f);
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(
            diagnostics.Select(diagnostic => diagnostic.GetMessage()),
            Is.EqualTo(new[]
            {
                "Argument has quantity 'unannotated scalar', but parameter 'value' requires 'length'"
            }));
    }

    [Test]
    public async Task CoalesceOfMatchingQuantitiesKeepsTheQuantity()
    {
        const string source = """
                              internal static class Physics
                              {
                                  internal static void Run(Metre? a, Metre b)
                                  {
                                      Metre valid = a ?? b;
                                      Second invalid = a ?? b;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(
            diagnostics.Single().GetMessage(),
            Is.EqualTo("Cannot assign 'length' to 'time'"));
    }

    [Test]
    public async Task CompoundAdditionAndSubtractionOfMatchingQuantitiesHaveNoDiagnostics()
    {
        const string source = """
                              internal static class Physics
                              {
                                  internal static void Run(Metre total, Metre step)
                                  {
                                      total += step;
                                      total -= step;
                                      total += 1f;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(diagnostics, Is.Empty);
    }

    [TestCase("+=")]
    [TestCase("-=")]
    public async Task CompoundAdditionAndSubtractionOfMismatchedQuantitiesAreQnt002(string op)
    {
        string source = $$"""
                          internal static class Physics
                          {
                              internal static void Run(Metre total, Second time)
                              {
                                  total {{op}} time;
                              }
                          }
                          """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        var diagnostic = diagnostics.Single();
        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Id, Is.EqualTo("QNT002"));
            Assert.That(diagnostic.GetMessage(), Is.EqualTo("Cannot assign 'time' to 'length'"));
        });
    }

    [Test]
    public async Task CompoundMultiplyAndDivideByDimensionlessValuesHaveNoDiagnostics()
    {
        const string source = """
                              internal static class Physics
                              {
                                  internal static void Run(Metre distance, Ratio ratio, Metre a, Metre b)
                                  {
                                      distance *= 2f;
                                      distance /= 2f;
                                      distance *= ratio;
                                      distance /= ratio;
                                      distance *= a / b;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task CompoundMultiplyAndDivideByDimensionedValuesAreQnt002()
    {
        const string source = """
                              internal static class Physics
                              {
                                  internal static void Run(Metre distance, Second time)
                                  {
                                      distance *= time;
                                      distance /= time;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(
            diagnostics.Select(diagnostic => diagnostic.GetMessage()),
            Is.EquivalentTo(new[]
            {
                "Cannot assign 'length^1 time^1' to 'length'",
                "Cannot assign 'length^1 time^-1' to 'length'"
            }));
    }

    [Test]
    public async Task CompoundAssignmentWithUnannotatedValueIsAccepted()
    {
        const string source = """
                              internal static class Physics
                              {
                                  internal static void Run(Metre distance, float raw)
                                  {
                                      distance += raw;
                                      distance *= raw;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task FieldAssignmentIsQnt002()
    {
        const string source = """
                              internal static class Physics
                              {
                                  private static Kelvin _target;

                                  internal static void Run(Pascal pressure)
                                  {
                                      _target = pressure;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        var diagnostic = diagnostics.Single();
        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Id, Is.EqualTo("QNT002"));
            Assert.That(
                diagnostic.GetMessage(),
                Is.EqualTo("Cannot assign 'length^-1 mass^1 time^-2' to 'temperature'"));
        });
    }

    [Test]
    public async Task PropertyAssignmentIsQnt002()
    {
        const string source = """
                              internal sealed class Sensor
                              {
                                  internal Kelvin Target { get; set; }

                                  internal void Run(Pascal pressure)
                                  {
                                      Target = pressure;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        var diagnostic = diagnostics.Single();
        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Id, Is.EqualTo("QNT002"));
            Assert.That(
                diagnostic.GetMessage(),
                Is.EqualTo("Cannot assign 'length^-1 mass^1 time^-2' to 'temperature'"));
        });
    }

    [Test]
    public async Task LocalAssignmentIsReportedOnTheWholeAssignment()
    {
        const string source = """
                              internal static class Physics
                              {
                                  internal static void Run(Pascal pressure)
                                  {
                                      Kelvin t;
                                      t = pressure;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        var diagnostic = diagnostics.Single();
        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Id, Is.EqualTo("QNT002"));
            Assert.That(SpanText(diagnostic), Is.EqualTo("t = pressure"));
        });
    }

    [Test]
    public async Task InitializerIsReportedOnTheValue()
    {
        const string source = """
                              internal static class Physics
                              {
                                  internal static void Run(Pascal pressure)
                                  {
                                      Kelvin t = pressure;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        var diagnostic = diagnostics.Single();
        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Id, Is.EqualTo("QNT002"));
            Assert.That(SpanText(diagnostic), Is.EqualTo("pressure"));
        });
    }

    [Test]
    public async Task FieldInitializerIsQnt002()
    {
        const string source = """
                              internal static class Physics
                              {
                                  private static readonly Pascal Ambient = 101325f;
                                  private static readonly Kelvin Target = Ambient;
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(
            diagnostics.Single().GetMessage(),
            Is.EqualTo("Cannot assign 'length^-1 mass^1 time^-2' to 'temperature'"));
    }

    [Test]
    public async Task PropertyInitializerIsQnt002()
    {
        const string source = """
                              internal sealed class Gas
                              {
                                  private static Pascal Ambient => 101325f;
                                  internal Kelvin Target { get; } = Ambient;
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        var diagnostic = diagnostics.Single();
        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Id, Is.EqualTo("QNT002"));
            Assert.That(SpanText(diagnostic), Is.EqualTo("Ambient"));
        });
    }

    [Test]
    public async Task MatchingAndLiteralFieldAndPropertyInitializersHaveNoDiagnostics()
    {
        const string source = """
                              internal sealed class Gas
                              {
                                  private static readonly Pascal Ambient = 101325f;
                                  private static readonly Pascal Copy = Ambient;
                                  private readonly Kelvin _temperature = 300f;
                                  internal Pascal Pressure { get; } = Ambient;
                                  internal Kelvin Start { get; set; } = 273.15f;
                                  private const float Raw = 1f;
                                  private readonly float _plain = 2f;
                                  private readonly Kelvin _claimed = Raw;
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task MultiFieldDeclarationReportsOnlyTheBadInitializer()
    {
        const string source = """
                              internal sealed class Gas
                              {
                                  private static readonly Kelvin X = 1f;
                                  private static readonly Pascal Y = 2f;
                                  private readonly Kelvin _a = X, _b = Y, _c = X;
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        var diagnostic = diagnostics.Single();
        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Id, Is.EqualTo("QNT002"));
            Assert.That(SpanText(diagnostic), Is.EqualTo("Y"));
        });
    }

    [Test]
    public async Task QuantityAttributedFieldInitializerIsChecked()
    {
        const string source = """
                              using Magnitudo;

                              internal sealed class Gas
                              {
                                  private static readonly Pascal Ambient = 101325f;
                                  [Quantity("temperature")] private readonly float _target = Ambient;
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        var diagnostic = diagnostics.Single();
        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Id, Is.EqualTo("QNT002"));
            Assert.That(SpanText(diagnostic), Is.EqualTo("Ambient"));
        });
    }

    [Test]
    public async Task ConstAndEnumInitializersHaveNoDiagnostics()
    {
        const string source = """
                              internal enum Mode
                              {
                                  A = 1,
                                  B = A + 1
                              }

                              internal sealed class Gas
                              {
                                  private const Kelvin Zero = 0f;
                                  private const int Count = 3;
                                  internal event System.Action? Changed = null;
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task PrimaryConstructorParameterInFieldInitializerIsChecked()
    {
        const string source = """
                              internal sealed class Gas(Pascal pressure)
                              {
                                  private readonly Kelvin _t = pressure;
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        var diagnostic = diagnostics.Single();
        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Id, Is.EqualTo("QNT002"));
            Assert.That(SpanText(diagnostic), Is.EqualTo("pressure"));
        });
    }

    [Test]
    public async Task MatchingPrimaryConstructorParameterInFieldInitializerHasNoDiagnostics()
    {
        const string source = """
                              internal sealed class Gas(Pascal pressure)
                              {
                                  private readonly Pascal _p = pressure;
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task PrimaryConstructorParameterInPropertyInitializerIsChecked()
    {
        const string source = """
                              internal sealed class Gas(Pascal pressure)
                              {
                                  internal Kelvin T { get; } = pressure;
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        var diagnostic = diagnostics.Single();
        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Id, Is.EqualTo("QNT002"));
            Assert.That(SpanText(diagnostic), Is.EqualTo("pressure"));
        });
    }

    [Test]
    public async Task PositionalRecordParameterCarriesQuantityIntoPropertyInitializer()
    {
        // The generated property's only declaring syntax is the ParameterSyntax, so GetSyntaxQuantity
        // reads the alias from the parameter type and the property keeps its quantity.
        const string source = """
                              internal sealed record Reading(Pascal Pressure)
                              {
                                  internal Kelvin T { get; } = Pressure;
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        var diagnostic = diagnostics.Single();
        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Id, Is.EqualTo("QNT002"));
            Assert.That(SpanText(diagnostic), Is.EqualTo("Pressure"));
        });
    }

    [Test]
    public async Task ConstInitializerWithQuantityMismatchIsQnt002()
    {
        const string source = """
                              internal static class Physics
                              {
                                  private const Pascal P = 1f;
                                  private const Kelvin K = P;
                                  private const Kelvin K2 = K;
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        var diagnostic = diagnostics.Single();
        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Id, Is.EqualTo("QNT002"));
            Assert.That(SpanText(diagnostic), Is.EqualTo("P"));
        });
    }

    [Test]
    public async Task ArrayElementAssignmentIsChecked()
    {
        const string source = """
                              internal static class Physics
                              {
                                  internal static void Run(Metre[] heights, Second time)
                                  {
                                      heights[0] = time;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(
            diagnostics.Single().GetMessage(),
            Is.EqualTo("Cannot assign 'time' to 'length'"));
    }

    [Test]
    public async Task QuantityAttributeOnPlainFloatMembersDeclaresTheQuantity()
    {
        const string source = """
                              using Magnitudo;

                              internal sealed class Sensor
                              {
                                  [Quantity("temperature")] private float _field;
                                  [Quantity("temperature")] internal float Property { get; set; }

                                  [return: Quantity("temperature")]
                                  private float Read() => 0f;

                                  private void Accept([Quantity("temperature")] float value) { }

                                  internal void Run(Pascal pressure, Kelvin temperature)
                                  {
                                      _field = pressure;
                                      Property = pressure;
                                      Pascal p = Read();
                                      Accept(pressure);
                                      _field = temperature;
                                      Property = temperature;
                                      Kelvin t = Read();
                                      Accept(temperature);
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(
            diagnostics.Select(diagnostic => diagnostic.Id),
            Is.EquivalentTo(new[] { "QNT002", "QNT002", "QNT002", "QNT003" }));
    }

    [Test]
    public async Task QuantityAttributeWinsOverAnAliasType()
    {
        const string source = """
                              using Magnitudo;

                              internal static class Physics
                              {
                                  [Quantity("pressure")] private static Kelvin _mixed;

                                  internal static void Run(Pascal pressure, Kelvin temperature)
                                  {
                                      _mixed = pressure;
                                      _mixed = temperature;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(
            diagnostics.Single().GetMessage(),
            Is.EqualTo("Cannot assign 'temperature' to 'length^-1 mass^1 time^-2'"));
    }

    [Test]
    public async Task UnknownQuantityIdInAttributeDeclaresNothing()
    {
        const string source = """
                              using Magnitudo;

                              internal static class Physics
                              {
                                  [Quantity("nonsense")] private static float _value;

                                  internal static void Run(Pascal pressure)
                                  {
                                      _value = pressure;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(
            diagnostics.Single().GetMessage(),
            Is.EqualTo("Cannot assign 'length^-1 mass^1 time^-2' to 'unannotated scalar'"));
    }

    [Test]
    public async Task ReturnStatementIsQnt004OnTheReturnedExpression()
    {
        const string source = """
                              internal static class Physics
                              {
                                  internal static Kelvin Read(Pascal pressure)
                                  {
                                      return pressure;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        var diagnostic = diagnostics.Single();
        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Id, Is.EqualTo("QNT004"));
            Assert.That(
                diagnostic.GetMessage(),
                Is.EqualTo(
                    "Returned expression has quantity 'length^-1 mass^1 time^-2', but the method returns 'temperature'"));
            Assert.That(SpanText(diagnostic), Is.EqualTo("pressure"));
        });
    }

    [Test]
    public async Task ExpressionBodiedMethodIsQnt004()
    {
        const string source = """
                              internal static class Physics
                              {
                                  internal static Kelvin Read(Pascal pressure) => pressure;
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(diagnostics.Single().Id, Is.EqualTo("QNT004"));
    }

    [Test]
    public async Task PropertyGettersAreCheckedAgainstTheProperty()
    {
        const string source = """
                              internal sealed class Sensor
                              {
                                  private readonly Pascal _pressure = 0f;

                                  internal Kelvin Block
                                  {
                                      get { return _pressure; }
                                  }

                                  internal Kelvin Expression => _pressure;
                                  internal Pascal Valid => _pressure;
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(
            diagnostics.Select(diagnostic => diagnostic.Id),
            Is.EqualTo(Enumerable.Repeat("QNT004", 2)));
    }

    [Test]
    public async Task PlainFloatReturnTypeCannotReturnAQuantity()
    {
        const string source = """
                              internal static class Physics
                              {
                                  internal static float Raw(Pascal pressure) => pressure;
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(
            diagnostics.Single().GetMessage(),
            Is.EqualTo(
                "Returned expression has quantity 'length^-1 mass^1 time^-2', but the method returns 'unannotated scalar'"));
    }

    [Test]
    public async Task QuantityReturnTypeAcceptsRawFloatAndLiteral()
    {
        const string source = """
                              internal static class Physics
                              {
                                  internal static Kelvin FromRaw(float raw) => raw;
                                  internal static Kelvin Zero() => 0f;
                                  internal static Kelvin Default() => default;
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task NamedArgumentsAreMatchedToTheirParameters()
    {
        const string source = """
                              internal static class Physics
                              {
                                  private static void Set(Kelvin first, Pascal second) { }

                                  internal static void Run(Pascal pressure, Kelvin temperature)
                                  {
                                      Set(second: pressure, first: temperature);
                                      Set(second: temperature, first: pressure);
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(
            diagnostics.Select(diagnostic => diagnostic.GetMessage()),
            Is.EquivalentTo(new[]
            {
                "Argument has quantity 'temperature', but parameter 'second' requires 'length^-1 mass^1 time^-2'",
                "Argument has quantity 'length^-1 mass^1 time^-2', but parameter 'first' requires 'temperature'"
            }));
    }

    [Test]
    public async Task ArgumentIsReportedOnTheArgumentExpression()
    {
        const string source = """
                              internal static class Physics
                              {
                                  private static void Set(Kelvin value) { }

                                  internal static void Run(Pascal pressure)
                                  {
                                      Set(value: pressure * 2f);
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(SpanText(diagnostics.Single()), Is.EqualTo("pressure * 2f"));
    }

    [Test]
    public async Task ParamsArrayElementsAreNotChecked()
    {
        // The parameter type is an array, so the parameter itself has no quantity.
        const string source = """
                              internal static class Physics
                              {
                                  private static void Many(params Metre[] values) { }

                                  internal static void Run(Metre distance, Second time)
                                  {
                                      Many(distance, time);
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task ConstructorArgumentsAreChecked()
    {
        const string source = """
                              internal sealed class Sensor
                              {
                                  internal Sensor(Kelvin initial) { }
                                  internal Sensor(Kelvin initial, Pascal ambient) : this(ambient) { }
                              }

                              internal static class Physics
                              {
                                  internal static void Run(Pascal pressure, Kelvin temperature)
                                  {
                                      _ = new Sensor(temperature);
                                      _ = new Sensor(pressure);
                                      Sensor typed = new(pressure);
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(
            diagnostics.Select(diagnostic => diagnostic.Id),
            Is.EqualTo(Enumerable.Repeat("QNT003", 3)));
    }

    [Test]
    public async Task VarCannotHoldAQuantity()
    {
        const string source = """
                              internal static class Physics
                              {
                                  internal static void Run(Pascal pressure)
                                  {
                                      var inferred = pressure;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        var diagnostic = diagnostics.Single();
        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Id, Is.EqualTo("QNT002"));
            Assert.That(
                diagnostic.GetMessage(),
                Is.EqualTo("Cannot assign 'length^-1 mass^1 time^-2' to 'unannotated scalar'"));
        });
    }

    [Test]
    public async Task FloatLocalCannotHoldAQuantityEvenThroughACast()
    {
        const string source = """
                              internal static class Physics
                              {
                                  internal static void Run(Pascal pressure)
                                  {
                                      float a = pressure;
                                      float b = (float)pressure;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(
            diagnostics.Select(diagnostic => diagnostic.Id),
            Is.EqualTo(Enumerable.Repeat("QNT002", 2)));
    }

    [Test]
    public async Task RawFloatAssignsToATypedLocalButDoesNotPassToATypedParameter()
    {
        const string source = """
                              internal static class Physics
                              {
                                  private static void SetTarget(Kelvin value) { }

                                  internal static void Run(float raw)
                                  {
                                      Kelvin t = raw;
                                      SetTarget(t);
                                      SetTarget(raw);
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        var diagnostic = diagnostics.Single();
        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Id, Is.EqualTo("QNT003"));
            Assert.That(
                diagnostic.GetMessage(),
                Is.EqualTo("Argument has quantity 'unannotated scalar', but parameter 'value' requires 'temperature'"));
        });
    }

    [Test]
    public async Task LiteralsAndDefaultPassToATypedParameter()
    {
        const string source = """
                              internal static class Physics
                              {
                                  private static void SetTarget(Kelvin value) { }

                                  internal static void Run()
                                  {
                                      SetTarget(0f);
                                      SetTarget(default);
                                      SetTarget(1f + 2f);
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task DimensionlessQuantityPassesToAnUnannotatedParameter()
    {
        const string source = """
                              internal static class Physics
                              {
                                  private static void Log(float value) { }

                                  internal static void Run(Metre a, Metre b, Pascal pressure)
                                  {
                                      Log(a / b);
                                      Log(pressure);
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task ElementQuantityAttributeOnParametersAndFieldsDeclaresElements()
    {
        const string source = """
                              using Magnitudo;

                              internal static class Physics
                              {
                                  [ElementQuantity("length")] private static float[] _heights = new float[1];

                                  internal static void Run([ElementQuantity("time")] float[] times)
                                  {
                                      Metre a = times[0];
                                      Second b = _heights[0];
                                      Metre c = _heights[0];
                                      Second d = times[0];
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(
            diagnostics.Select(diagnostic => diagnostic.GetMessage()),
            Is.EquivalentTo(new[]
            {
                "Cannot assign 'time' to 'length'",
                "Cannot assign 'length' to 'time'"
            }));
    }

    [Test]
    public async Task EmptyCatalogDisablesTheAnalyzer()
    {
        const string source = """
                              internal static class Physics
                              {
                                  internal static float Run(float a, float b) => a + b;
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, string.Empty);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task CommentsAndBlankLinesInTheCatalogAreIgnored()
    {
        const string source = """
                              internal static class Physics
                              {
                                  internal static void Run(Metre distance)
                                  {
                                      Second time = distance;
                                  }
                              }
                              """;

        const string catalog = """
                               # Base quantities

                               length | Metre | global::System.Single | length=1
                                   # indented comment
                               # time | Hidden | global::System.Single | time=1

                               time | Second | global::System.Single | time=1
                               """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, catalog);

        Assert.That(diagnostics.Single().GetMessage(), Is.EqualTo("Cannot assign 'length' to 'time'"));
    }

    [Test]
    public async Task CatalogToleratesTabsExtraSpacesAndCrLf()
    {
        const string source = """
                              internal static class Physics
                              {
                                  internal static void Run(Metre distance)
                                  {
                                      Second time = distance;
                                  }
                              }
                              """;

        const string catalog = "\t length \t|  Metre|global::System.Single  |length = 1\r\n" +
                               "   # comment\r\n" +
                               "\r\n" +
                               "time|Second|global::System.Single|time=1  \r\n";

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, catalog);

        Assert.That(diagnostics.Single().GetMessage(), Is.EqualTo("Cannot assign 'length' to 'time'"));
    }

    [TestCase("1")]
    [TestCase("")]
    [TestCase("length=0,time=0")]
    public async Task DimensionlessCatalogFormsAreScalar(string dimensions)
    {
        const string source = """
                              internal static class Physics
                              {
                                  internal static void Run(Ratio a, Ratio b)
                                  {
                                      Ratio product = a * b;
                                      float raw = a / b;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(
            source,
            $"ratio | Ratio | global::System.Single | {dimensions}");

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task ZeroExponentIsDroppedSoAliasesWithTheSameNonzeroExponentsMatch()
    {
        const string source = """
                              internal static class Physics
                              {
                                  internal static void Run(Metre a, Padded b)
                                  {
                                      Metre x = b;
                                      Padded y = a;
                                  }
                              }
                              """;

        const string catalog = """
                               length | Metre | global::System.Single | length=1
                               padded | Padded | global::System.Single | length=1,time=0
                               """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, catalog);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task AnyBaseDimensionNameIsAcceptedWithoutDeclaration()
    {
        const string source = """
                              internal static class Physics
                              {
                                  internal static Money Run(Money a, Tick t) => a * t;
                              }
                              """;

        const string catalog = """
                               currency | Money | global::System.Single | currency=1
                               tick | Tick | global::System.Single | tick=1
                               """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, catalog);

        Assert.That(
            diagnostics.Single().GetMessage(),
            Is.EqualTo("Returned expression has quantity 'currency^1 tick^1', but the method returns 'currency'"));
    }

    [Test]
    public async Task SameDimensionWithDifferentMeaningProducesNoDiagnostic()
    {
        const string source = """
                              internal static class Physics
                              {
                                  internal static void Run(NewtonMetre torque, Joule energy)
                                  {
                                      Joule a = torque;
                                      NewtonMetre b = energy;
                                      Joule c = torque + energy;
                                      _ = torque < energy;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, MechanicsCatalog);

        Assert.That(diagnostics, Is.Empty);
    }

    [TestCase("mass", "mass")]
    [TestCase("distance * distance", "length^2")]
    [TestCase("1f / time", "time^-1")]
    [TestCase("mass * distance", "length^1 mass^1")]
    [TestCase("distance / time", "length^1 time^-1")]
    [TestCase("mass * distance / (time * time)", "length^1 mass^1 time^-2")]
    [TestCase("mass * distance * distance / (time * time)", "length^2 mass^1 time^-2")]
    [TestCase("distance / distance", "dimensionless")]
    public async Task MessagesPrintSortedExponentVectors(string expression, string printed)
    {
        string source = $$"""
                          internal static class Physics
                          {
                              internal static void Run(Kilogram mass, Metre distance, Second time)
                              {
                                  MetrePerSecond target = {{expression}};
                                  Second wrong = {{expression}};
                              }
                          }
                          """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, MechanicsCatalog);

        Assert.That(
            diagnostics.Select(diagnostic => diagnostic.GetMessage()),
            Does.Contain($"Cannot assign '{printed}' to 'time'"));
    }

    [Test]
    public async Task QuantityAttributesOnReferencedAssemblyApiAreHonored()
    {
        const string library = """
                               using Magnitudo;

                               public static class Lib
                               {
                                   [Quantity("temperature")] public static float Ambient;

                                   [return: Quantity("length")]
                                   public static float Reach() => 1f;

                                   public static void Set([Quantity("temperature")] float value) { }
                               }
                               """;

        const string source = """
                              internal static class Physics
                              {
                                  internal static void Run(Pascal pressure, Kelvin temperature)
                                  {
                                      Lib.Set(temperature);
                                      Lib.Set(Lib.Ambient);
                                      Lib.Set(pressure);
                                      Second wrong = Lib.Reach();
                                      Metre valid = Lib.Reach();
                                      Lib.Ambient = pressure;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(
            source,
            Catalog,
            CompileLibrary(library, Catalog));

        Assert.That(
            diagnostics.Select(diagnostic => diagnostic.Id),
            Is.EquivalentTo(new[] { "QNT003", "QNT002", "QNT002" }));
    }

    [Test]
    public async Task ElementQuantityOnReferencedAssemblyApiIsHonored()
    {
        const string library = """
                               using Magnitudo;

                               public static class Lib
                               {
                                   [ElementQuantity("length")] public static float[] Heights = new float[1];

                                   [return: ElementQuantity("length")]
                                   public static float[] Read() => new float[1];
                               }
                               """;

        const string source = """
                              internal static class Physics
                              {
                                  internal static void Run()
                                  {
                                      Second a = Lib.Heights[0];
                                      Second b = Lib.Read()[0];
                                      Metre c = Lib.Heights[0];
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(
            source,
            Catalog,
            CompileLibrary(library, Catalog));

        Assert.That(
            diagnostics.Select(diagnostic => diagnostic.GetMessage()),
            Is.EqualTo(Enumerable.Repeat("Cannot assign 'length' to 'time'", 2)));
    }

    [Test]
    public async Task AliasTypedParameterInReferencedAssemblyAcceptsAnything()
    {
        const string library = """
                               public static class Lib
                               {
                                   public static void Set(Kelvin value) { }
                                   public static Kelvin Read() => 0f;
                                   public static Kelvin Ambient;
                               }
                               """;

        const string source = """
                              internal static class Physics
                              {
                                  internal static void Run(Pascal pressure, float raw)
                                  {
                                      Lib.Set(pressure);
                                      Lib.Set(raw);
                                      Pascal p = Lib.Read();
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(
            source,
            Catalog,
            CompileLibrary(library, Catalog));

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task AssigningAQuantityToAnAliasFieldInAReferencedAssemblyIsUnannotated()
    {
        const string library = """
                               public static class Lib
                               {
                                   public static Kelvin Ambient;
                               }
                               """;

        const string source = """
                              internal static class Physics
                              {
                                  internal static void Run(Pascal pressure)
                                  {
                                      Lib.Ambient = pressure;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(
            source,
            Catalog,
            CompileLibrary(library, Catalog));

        Assert.That(
            diagnostics.Single().GetMessage(),
            Is.EqualTo("Cannot assign 'length^-1 mass^1 time^-2' to 'unannotated scalar'"));
    }

    [TestCase("pressure | Pascal")]
    [TestCase("pressure | Pascal | global::System.Single")]
    [TestCase("pressure | Pascal | global::System.Single | mass=1 | extra")]
    [TestCase("Convert | length | Metre | global::System.Single | length=1")]
    public void CatalogRejectsQuantityLinesWithTheWrongColumnCount(string line)
    {
        ImmutableArray<Diagnostic> diagnostics = ParseCatalog(line);

        Assert.That(
            diagnostics.Select(diagnostic => diagnostic.GetMessage()),
            Is.EqualTo(new[] { "Invalid units catalog: Expected 'id | alias | storage type | dimensions'." }));
    }

    [TestCase("9pressure | Pascal | global::System.Single | mass=1")]
    [TestCase("pressure | 9Pascal | global::System.Single | mass=1")]
    [TestCase("pressure | Pas-cal | global::System.Single | mass=1")]
    [TestCase(" | Pascal | global::System.Single | mass=1")]
    [TestCase("pressure |  | global::System.Single | mass=1")]
    [TestCase("pressure | Pascal |  | mass=1")]
    public void CatalogRejectsInvalidIdentifiersAndMissingStorageType(string line)
    {
        ImmutableArray<Diagnostic> diagnostics = ParseCatalog(line);

        Assert.That(
            diagnostics.Select(diagnostic => diagnostic.GetMessage()),
            Is.EqualTo(new[]
            {
                "Invalid units catalog: Quantity IDs and aliases must be identifiers and a storage type is required."
            }));
    }

    [TestCase("mass:1", "mass:1")]
    [TestCase("mass", "mass")]
    [TestCase("mass=", "mass=")]
    [TestCase("=1", "=1")]
    [TestCase("mass=abc", "mass=abc")]
    [TestCase("mass=1=2", "mass=1=2")]
    [TestCase("1mass=1", "1mass=1")]
    [TestCase("mass=1,", "")]
    [TestCase("mass=1,,time=1", "")]
    [TestCase("length=-1.5", "length=-1.5")]
    [TestCase("length=0.5", "length=0.5")]
    [TestCase("length=1/2", "length=1/2")]
    public void CatalogRejectsInvalidDimensionEntries(string dimensions, string entry)
    {
        ImmutableArray<Diagnostic> diagnostics = ParseCatalog($"pressure | Pascal | global::System.Single | {dimensions}");

        Assert.That(
            diagnostics.Select(diagnostic => diagnostic.GetMessage()),
            Is.EqualTo(new[] { $"Invalid units catalog: Invalid dimension entry '{entry}'." }));
    }

    [Test]
    public void CatalogAcceptsSignedAndLargeIntegerExponents()
    {
        ImmutableArray<Diagnostic> diagnostics = ParseCatalog(
            "a | A | global::System.Single | length=+2,time=-3,mass=10");

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public void CatalogRejectsDuplicateAliasWithADifferentId()
    {
        ImmutableArray<Diagnostic> diagnostics = ParseCatalog(
            "length | Metre | global::System.Single | length=1\n" +
            "distance | Metre | global::System.Single | length=1");

        Assert.That(
            diagnostics.Select(diagnostic => diagnostic.GetMessage()),
            Is.EqualTo(new[] { "Invalid units catalog: Alias 'Metre' is declared more than once." }));
    }

    [Test]
    public void CatalogDoesNotCheckTheStorageType()
    {
        var compilation = GenerateAliases(
            CreateCompilation("internal static class C { internal static Metre Value; }"),
            "length | Metre | global::System.Singel | length=1",
            out ImmutableArray<Diagnostic> diagnostics);

        var errors = compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(diagnostics, Is.Empty);
            Assert.That(errors, Is.Not.Empty);
            Assert.That(errors.Select(error => error.Id), Has.None.EqualTo("QNT000"));
        });
    }

    [Test]
    public void CatalogWithOnlyInvalidLinesGeneratesNothing()
    {
        var compilation = GenerateAliases(
            CreateCompilation("internal static class C { }"),
            "broken\nalso | broken",
            out ImmutableArray<Diagnostic> diagnostics);

        Assert.Multiple(() =>
        {
            Assert.That(diagnostics, Has.Length.EqualTo(2));
            Assert.That(
                compilation.SyntaxTrees.Select(tree => tree.FilePath),
                Has.None.Contain("QuantityAliases"));
        });
    }

    [Test]
    public void EmptyCatalogGeneratesNothingAndReportsNothing()
    {
        var compilation = GenerateAliases(
            CreateCompilation("internal static class C { }"),
            string.Empty,
            out ImmutableArray<Diagnostic> diagnostics);

        Assert.Multiple(() =>
        {
            Assert.That(diagnostics, Is.Empty);
            Assert.That(compilation.SyntaxTrees, Has.Exactly(1).Items);
        });
    }

    [Test]
    public void CatalogErrorIsReportedOnTheLineThatCausedIt()
    {
        ImmutableArray<Diagnostic> diagnostics = ParseCatalog(
            "# header\n\nlength | Metre | global::System.Single | length=1\nbad line");

        var diagnostic = diagnostics.Single();
        var span = diagnostic.Location.GetLineSpan();
        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Id, Is.EqualTo("QNT000"));
            Assert.That(span.Path, Is.EqualTo("Test.magnitudo"));
            Assert.That(span.StartLinePosition, Is.EqualTo(new LinePosition(3, 0)));
            Assert.That(span.EndLinePosition, Is.EqualTo(new LinePosition(3, "bad line".Length)));
        });
    }

    [Test]
    public void SkippedCatalogLineRemovesItsAliasAndLeavesTheRest()
    {
        var compilation = GenerateAliases(
            CreateCompilation("""
                              internal static class C
                              {
                                  internal static Pascal Pressure;
                                  internal static Metre Distance;
                              }
                              """),
            "pressure | Pascal | global::System.Single | length=-1.5\n" +
            "length | Metre | global::System.Single | length=1",
            out ImmutableArray<Diagnostic> diagnostics);

        var errors = compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(diagnostics.Select(diagnostic => diagnostic.Id), Is.EqualTo(new[] { "QNT000" }));
            Assert.That(errors.Select(error => error.Id), Is.EqualTo(new[] { "CS0246" }));
            Assert.That(errors.Single().GetMessage(), Does.Contain("Pascal"));
        });
    }

    [TestCase("convert | length | Inch | global::System.Single | 0 | 0",
        "Conversion scale must be a finite, nonzero number.")]
    [TestCase("convert | length | Inch | global::System.Single | abc | 0",
        "Conversion scale must be a finite, nonzero number.")]
    [TestCase("convert | length | Inch | global::System.Single | NaN | 0",
        "Conversion scale must be a finite, nonzero number.")]
    [TestCase("convert | length | Inch | global::System.Single | Infinity | 0",
        "Conversion scale must be a finite, nonzero number.")]
    [TestCase("convert | length | Inch | global::System.Single | 1 | abc",
        "Conversion offset must be a finite number.")]
    [TestCase("convert | length | Inch | global::System.Single | 1 | NaN",
        "Conversion offset must be a finite number.")]
    [TestCase("convert | length | Inch | global::System.Single | 1",
        "Expected 'convert | quantity id | unit name | storage type | scale | offset'.")]
    [TestCase("convert | length | 9Inch | global::System.Single | 1 | 0",
        "Conversion quantity IDs and unit names must be identifiers.")]
    [TestCase("convert | length | Inch |  | 1 | 0",
        "Conversion quantity IDs and unit names must be identifiers.")]
    public void CatalogRejectsInvalidConversionLines(string line, string message)
    {
        ImmutableArray<Diagnostic> diagnostics = ParseCatalog(
            "length | Metre | global::System.Single | length=1\n" + line);

        Assert.That(
            diagnostics.Select(diagnostic => diagnostic.GetMessage()),
            Is.EqualTo(new[] { "Invalid units catalog: " + message }));
    }

    [Test]
    public void CatalogRejectsDuplicateConversionUnit()
    {
        ImmutableArray<Diagnostic> diagnostics = ParseCatalog(
            "length | Metre | global::System.Single | length=1\n" +
            "convert | length | Inch | global::System.Single | 0.0254 | 0\n" +
            "convert | length | Inch | global::System.Single | 0.0254 | 0");

        Assert.That(
            diagnostics.Select(diagnostic => diagnostic.GetMessage()),
            Is.EqualTo(new[] { "Invalid units catalog: Conversion unit 'Inch' is declared more than once." }));
    }

    [Test]
    public void ConversionToAnUnknownQuantityHasNoLocation()
    {
        ImmutableArray<Diagnostic> diagnostics = ParseCatalog(
            "length | Metre | global::System.Single | length=1\n" +
            "convert | nope | Inch | global::System.Single | 1 | 0");

        var diagnostic = diagnostics.Single();
        Assert.Multiple(() =>
        {
            Assert.That(
                diagnostic.GetMessage(),
                Is.EqualTo("Invalid units catalog: Conversion 'Inch' references unknown quantity 'nope'."));
            Assert.That(diagnostic.Location, Is.EqualTo(Location.None));
        });
    }

    [Test]
    public void ValidConversionLineProducesBothDirections()
    {
        var compilation = GenerateAliases(
            CreateCompilation("""
                              internal static class C
                              {
                                  internal static float Run() =>
                                      Magnitudo.Generated.UnitConversions.ToInch(
                                          Magnitudo.Generated.UnitConversions.FromInch(1f));
                              }
                              """),
            "length | Metre | float | length=1\nconvert | length | Inch | float | 0.0254 | 0",
            out ImmutableArray<Diagnostic> diagnostics);

        Assert.Multiple(() =>
        {
            Assert.That(diagnostics, Is.Empty);
            Assert.That(
                compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
                Is.Empty);
        });
    }

    [Test]
    public async Task DeclarationFromAProjectReferenceDoesNotCrashTheAnalyzer()
    {
        const string library = """
                               public static class Lib
                               {
                                   public static Kelvin Ambient;
                                   public static Kelvin[] History = new Kelvin[1];
                               }
                               """;

        const string source = """
                              internal static class Physics
                              {
                                  internal static void Run(Pascal pressure)
                                  {
                                      Lib.Ambient = pressure;
                                      Pascal fromArray = Lib.History[0];
                                  }
                              }
                              """;

        var libraryCompilation = GenerateAliases(CreateCompilation(library, "Library"), Catalog, out _);

        // A compilation reference keeps the library's syntax trees, which the app compilation does not contain.
        ImmutableArray<Diagnostic> diagnostics = await Analyze(
            source,
            Catalog,
            libraryCompilation.ToMetadataReference());

        Assert.Multiple(() =>
        {
            Assert.That(diagnostics.Select(diagnostic => diagnostic.Id), Has.None.StartWith("AD"));
            Assert.That(
                diagnostics.Select(diagnostic => diagnostic.GetMessage()),
                Is.EqualTo(new[] { "Cannot assign 'length^-1 mass^1 time^-2' to 'unannotated scalar'" }));
        });
    }

    [Test]
    public async Task UserTypeNamedLikeAnAliasHasNoQuantity()
    {
        const string source = """
                              namespace App
                              {
                                  internal readonly struct Metre
                                  {
                                      public readonly float Value;
                                      public Metre(float value) => Value = value;
                                      public static Metre operator *(Metre a, Metre b) => new(a.Value * b.Value);
                                  }

                                  internal static class Geometry
                                  {
                                      internal static Metre Square(Metre side)
                                      {
                                          Metre area = side * side;
                                          return area;
                                      }
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task IndexerResultThatIsNotTheAliasTypeHasNoQuantity()
    {
        const string source = """
                              using System.Collections.Generic;
                              using System.Linq;

                              internal static class Physics
                              {
                                  internal static void Run(ILookup<string, Metre> lookup, Dictionary<string, Metre> map)
                                  {
                                      var group = lookup["a"];
                                      IEnumerable<Metre> typed = lookup["a"];
                                      Metre valid = map["a"];
                                      Second wrong = map["a"];
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(
            diagnostics.Select(diagnostic => diagnostic.GetMessage()),
            Is.EqualTo(new[] { "Cannot assign 'length' to 'time'" }));
    }

    [Test]
    public async Task CompoundAssignmentIntoAnUnannotatedTargetIsQnt002()
    {
        const string source = """
                              internal static class Physics
                              {
                                  internal static void Run(Pascal pressure)
                                  {
                                      float total = 0f;
                                      total += pressure;
                                      total -= pressure;
                                      total *= pressure;
                                      total /= pressure;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(
            diagnostics.Select(diagnostic => diagnostic.GetMessage()),
            Is.EqualTo(new[]
            {
                "Cannot assign 'length^-1 mass^1 time^-2' to 'unannotated scalar'",
                "Cannot assign 'length^-1 mass^1 time^-2' to 'unannotated scalar'",
                "Cannot assign 'length^-1 mass^1 time^-2' to 'unannotated scalar'",
                "Cannot assign 'length^1 mass^-1 time^2' to 'unannotated scalar'"
            }));
    }

    [Test]
    public async Task CompoundAssignmentIntoAnUnannotatedTargetAcceptsScalarsAndStrings()
    {
        const string source = """
                              internal static class Physics
                              {
                                  internal static void Run(Pascal pressure, Ratio ratio, float raw, Metre a, Metre b)
                                  {
                                      float total = 0f;
                                      total += 1f;
                                      total += raw;
                                      total *= ratio;
                                      total /= a / b;
                                      total *= 2f;
                                      string text = "";
                                      text += pressure;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task MessagesUseTheInvariantCulture()
    {
        const string source = """
                              internal static class Physics
                              {
                                  internal static Kelvin Run(Pascal pressure) => pressure;
                              }
                              """;

        CultureInfo culture = CultureInfo.CurrentCulture;
        CultureInfo uiCulture = CultureInfo.CurrentUICulture;
        try
        {
            // sv-SE writes the minus sign as U+2212.
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo("sv-SE");

            ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

            Assert.That(
                diagnostics.Single().GetMessage(),
                Is.EqualTo("Returned expression has quantity 'length^-1 mass^1 time^-2', but the method returns 'temperature'"));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
        }
    }

    [Test]
    public void GeneratedConversionsCompileWithDocumentationChecks()
    {
        var compilation = GenerateAliases(
            CreateCompilation(
                "/// <summary>Documented.</summary>\npublic static class C { }",
                documentationMode: DocumentationMode.Diagnose),
            "temperature | Kelvin | float | temperature=1\nconvert | temperature | Celsius | float | 1 | 273.15",
            out ImmutableArray<Diagnostic> generatorDiagnostics);

        Assert.That(generatorDiagnostics, Is.Empty);
        Assert.That(
            compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning),
            Is.Empty);
    }

    [Test]
    public void ConversionsInTwoReferencedProjectsDoNotConflict()
    {
        const string catalog =
            "temperature | Kelvin | float | temperature=1\nconvert | temperature | Celsius | float | 1 | 273.15";

        var library = GenerateAliases(CreateCompilation("public static class Lib { }", "Library"), catalog, out _);
        var app = GenerateAliases(
            CreateCompilation(
                "internal static class C { internal static Kelvin Run() => Magnitudo.Generated.UnitConversions.FromCelsius(1f); }",
                "App",
                [library.ToMetadataReference()]),
            catalog,
            out _);

        Assert.That(
            app.GetDiagnostics().Where(diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning),
            Is.Empty);
    }

    [Test]
    public void GeneratedSourceUsesLineFeedsAndNeutralWording()
    {
        var compilation = GenerateAliases(
            CreateCompilation("internal static class C { }"),
            "length | Metre | float | length=1\nconvert | length | Inch | float | 0.0254 | 0",
            out _);

        string generated = GeneratedSource(compilation);
        Assert.Multiple(() =>
        {
            Assert.That(generated, Does.Not.Contain("\r"));
            Assert.That(generated, Does.Contain("the catalog's canonical units"));
            Assert.That(generated, Does.Not.Contain("solver"));
        });
    }

    [TestCase("count | Count | int | count=1\nconvert | count | Dozen | int | 12 | 0",
        "Conversion 'Dozen' needs a float, double, or decimal storage type, not 'int'.")]
    [TestCase("count | Count | int | count=1\nconvert | count | Dozen | float? | 12 | 0",
        "Conversion 'Dozen' needs a float, double, or decimal storage type, not 'float?'.")]
    [TestCase("length | Metre | float | length=1\nconvert | length | Big | float | 1e39 | 0",
        "Conversion scale is outside the range of 'float'.")]
    [TestCase("length | Metre | float | length=1\nconvert | length | Tiny | float | 1e-50 | 0",
        "Conversion scale rounds to zero in 'float'.")]
    [TestCase("length | Metre | float | length=1\nconvert | length | Far | float | 1 | 1e39",
        "Conversion offset is outside the range of 'float'.")]
    [TestCase("length | Metre | decimal | length=1\nconvert | length | Big | decimal | 1e30 | 0",
        "Conversion scale is outside the range of 'decimal'.")]
    [TestCase("length | Metre | decimal | length=1\nconvert | length | Tiny | decimal | 1e-30 | 0",
        "Conversion scale rounds to zero in 'decimal'.")]
    [TestCase("length | Metre | decimal | length=1\nconvert | length | Far | System.Decimal | 1 | -1e30",
        "Conversion offset is outside the range of 'System.Decimal'.")]
    public void CatalogRejectsConversionsThatDoNotFitTheirStorage(string catalog, string message)
    {
        ImmutableArray<Diagnostic> diagnostics = ParseCatalog(catalog);

        Assert.That(
            diagnostics.Select(diagnostic => diagnostic.GetMessage()),
            Is.EqualTo(new[] { "Invalid units catalog: " + message }));
    }

    [Test]
    public void ConversionsInDoubleAndDecimalStorageCompile()
    {
        var compilation = GenerateAliases(
            CreateCompilation("internal static class C { }"),
            "length | Metre | double | length=1\n" +
            "convert | length | Inch | double | 0.0254 | 0\n" +
            "convert | length | Mile | System.Decimal | 1609.344 | 0\n" +
            "convert | length | Micro | decimal | 1e-6 | 1e-3\n" +
            "convert | length | Parsec | System.Single | 3.0857e16 | 0",
            out ImmutableArray<Diagnostic> diagnostics);

        Assert.Multiple(() =>
        {
            Assert.That(diagnostics, Is.Empty);
            Assert.That(
                compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
                Is.Empty);
        });
    }

    [TestCase("var")]
    [TestCase("dynamic")]
    [TestCase("nint")]
    [TestCase("nuint")]
    [TestCase("_")]
    [TestCase("class")]
    [TestCase("float")]
    public void CatalogRejectsKeywordAliasNames(string alias)
    {
        ImmutableArray<Diagnostic> diagnostics = ParseCatalog($"length | {alias} | float | length=1");

        Assert.That(
            diagnostics.Select(diagnostic => diagnostic.GetMessage()),
            Is.EqualTo(new[] { $"Invalid units catalog: Alias '{alias}' is a C# keyword." }));
    }

    [Test]
    public void CatalogWithOnlyConversionsReportsTheUnknownQuantity()
    {
        var compilation = GenerateAliases(
            CreateCompilation("internal static class C { }"),
            "convert | length | Inch | float | 0.0254 | 0",
            out ImmutableArray<Diagnostic> diagnostics);

        Assert.Multiple(() =>
        {
            Assert.That(
                diagnostics.Select(diagnostic => diagnostic.GetMessage()),
                Is.EqualTo(new[]
                {
                    "Invalid units catalog: Conversion 'Inch' references unknown quantity 'length'."
                }));
            Assert.That(GeneratedSource(compilation), Is.Empty);
        });
    }

    private const string ConversionCatalog =
        "temperature | Kelvin | float | temperature=1\n" +
        "pressure | Pascal | float | mass=1,length=-1,time=-2\n" +
        "convert | temperature | Celsius | float | 1 | 273.15";

    private static IEnumerable<Diagnostic> WarningsAndErrors(Compilation compilation)
    {
        return compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning);
    }

    [Test]
    public async Task PublicHostInANamespaceIsCheckedByTheAnalyzer()
    {
        const string source = """
                              namespace Numos.Units;

                              [Magnitudo.UnitConversions]
                              public static partial class Temperatures;

                              internal static class Use
                              {
                                  internal static Kelvin Good() => Temperatures.FromCelsius(20f);
                                  internal static Pascal Bad() => Temperatures.FromCelsius(20f);
                                  internal static float BadArgument(Pascal pressure) => Temperatures.ToCelsius(pressure);
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, ConversionCatalog);

        Assert.That(
            diagnostics.Select(diagnostic => diagnostic.Id),
            Is.EquivalentTo(new[] { "QNT004", "QNT003" }));
    }

    [Test]
    public void PublicHostBuildsCleanWithDocumentationChecks()
    {
        var compilation = GenerateAliases(
            CreateCompilation(
                """
                namespace Numos.Units;

                /// <summary>Host.</summary>
                [Magnitudo.UnitConversions]
                public static partial class Temperatures;
                """,
                documentationMode: DocumentationMode.Diagnose),
            ConversionCatalog,
            out ImmutableArray<Diagnostic> generatorDiagnostics);

        Assert.Multiple(() =>
        {
            Assert.That(generatorDiagnostics, Is.Empty);
            Assert.That(WarningsAndErrors(compilation), Is.Empty);
            Assert.That(
                compilation.GetTypeByMetadataName("Numos.Units.Temperatures")!.DeclaredAccessibility,
                Is.EqualTo(Accessibility.Public));
        });
    }

    [Test]
    public void HostWithoutAModifierIsInternal()
    {
        var compilation = GenerateAliases(
            CreateCompilation("[Magnitudo.UnitConversions]\nstatic partial class Temperatures;"),
            ConversionCatalog,
            out ImmutableArray<Diagnostic> generatorDiagnostics);

        Assert.Multiple(() =>
        {
            Assert.That(generatorDiagnostics, Is.Empty);
            Assert.That(WarningsAndErrors(compilation), Is.Empty);
            Assert.That(
                compilation.GetTypeByMetadataName("Temperatures")!.DeclaredAccessibility,
                Is.EqualTo(Accessibility.Internal));
        });
    }

    [Test]
    public void HostInTheGlobalNamespaceReceivesConversions()
    {
        var compilation = GenerateAliases(
            CreateCompilation("""
                              [Magnitudo.UnitConversions]
                              public static partial class Temperatures;

                              internal static class Use
                              {
                                  internal static Kelvin Run() => Temperatures.FromCelsius(1f);
                              }
                              """),
            ConversionCatalog,
            out ImmutableArray<Diagnostic> generatorDiagnostics);

        Assert.Multiple(() =>
        {
            Assert.That(generatorDiagnostics, Is.Empty);
            Assert.That(WarningsAndErrors(compilation), Is.Empty);
            Assert.That(
                compilation.SyntaxTrees.Single(tree => tree.FilePath.Contains("Magnitudo.UnitConversions.Temperatures"))
                    .ToString(),
                Does.Not.Contain("namespace"));
        });
    }

    [Test]
    public void DefaultClassIsSkippedWhenAHostExists()
    {
        var withHost = GenerateAliases(
            CreateCompilation("[Magnitudo.UnitConversions]\nstatic partial class Temperatures;"),
            ConversionCatalog,
            out _);
        var withoutHost = GenerateAliases(CreateCompilation("static class C { }"), ConversionCatalog, out _);

        Assert.Multiple(() =>
        {
            Assert.That(withHost.GetTypeByMetadataName("Magnitudo.Generated.UnitConversions"), Is.Null);
            Assert.That(withoutHost.GetTypeByMetadataName("Magnitudo.Generated.UnitConversions"), Is.Not.Null);
        });
    }

    [TestCase("[Magnitudo.UnitConversions]\npartial class Temperatures { }", "Temperatures")]
    [TestCase("[Magnitudo.UnitConversions]\nstatic class Temperatures { }", "Temperatures")]
    [TestCase("[Magnitudo.UnitConversions]\nstatic partial class Temperatures<T> { }", "Temperatures")]
    [TestCase("static partial class Outer\n{\n    [Magnitudo.UnitConversions]\n    static partial class Temperatures { }\n}",
        "Temperatures")]
    public void InvalidHostReportsQnt005(string source, string identifier)
    {
        var compilation = GenerateAliases(
            CreateCompilation(source),
            ConversionCatalog,
            out ImmutableArray<Diagnostic> diagnostics);

        Assert.Multiple(() =>
        {
            Assert.That(diagnostics.Select(diagnostic => diagnostic.Id), Is.EqualTo(new[] { "QNT005" }));
            Assert.That(SpanText(diagnostics.Single()), Is.EqualTo(identifier));
            Assert.That(diagnostics.Single().Severity, Is.EqualTo(DiagnosticSeverity.Error));
            Assert.That(
                compilation.SyntaxTrees.Where(tree => tree.FilePath.Contains("Magnitudo.UnitConversions.")),
                Is.Empty);
            Assert.That(compilation.GetTypeByMetadataName("Magnitudo.Generated.UnitConversions"), Is.Null);
        });
    }

    [Test]
    public void LibraryHostWithInternalsVisibleToDoesNotClashWithTheApp()
    {
        const string library = """
                               [assembly: System.Runtime.CompilerServices.InternalsVisibleTo("App")]

                               [Magnitudo.UnitConversions]
                               public static partial class Temperatures;
                               """;

        var libraryCompilation = GenerateAliases(CreateCompilation(library, "Library"), ConversionCatalog, out _);
        var app = GenerateAliases(
            CreateCompilation(
                "internal static class C { internal static Kelvin Run() => Temperatures.FromCelsius(1f); }",
                "App",
                [libraryCompilation.ToMetadataReference()]),
            ConversionCatalog,
            out ImmutableArray<Diagnostic> diagnostics);

        Assert.Multiple(() =>
        {
            Assert.That(diagnostics, Is.Empty);
            Assert.That(WarningsAndErrors(app), Is.Empty);
        });
    }

    [Test]
    public async Task AppChecksQuantitiesOfALibraryHostFromMetadata()
    {
        MetadataReference library = CompileLibrary(
            "[Magnitudo.UnitConversions]\npublic static partial class Temperatures;",
            ConversionCatalog);

        ImmutableArray<Diagnostic> diagnostics = await Analyze(
            "internal static class C { internal static Pascal Run() => Temperatures.FromCelsius(1f); }",
            ConversionCatalog,
            library);

        Assert.That(diagnostics.Select(diagnostic => diagnostic.Id), Is.EqualTo(new[] { "QNT004" }));
    }

    [Test]
    public void PartialHostWithTwoDeclarationsIsGeneratedOnce()
    {
        var compilation = GenerateAliases(
            CreateCompilation("""
                              [Magnitudo.UnitConversions]
                              public static partial class Temperatures;

                              public static partial class Temperatures
                              {
                                  public static float Extra() => 1f;
                              }
                              """),
            ConversionCatalog,
            out ImmutableArray<Diagnostic> diagnostics);

        Assert.Multiple(() =>
        {
            Assert.That(diagnostics, Is.Empty);
            Assert.That(WarningsAndErrors(compilation), Is.Empty);
            Assert.That(
                compilation.SyntaxTrees.Count(tree => tree.FilePath.Contains("Magnitudo.UnitConversions.")),
                Is.EqualTo(1));
        });
    }

    [Test]
    public async Task VectorTypeArgumentCarriesTheLaneQuantity()
    {
        const string source = """
                              using System.Numerics;
                              using System.Runtime.Intrinsics;

                              internal static class Lanes
                              {
                                  internal static Vector<float> Sum(Vector<Pascal> p, Vector<Kelvin> t) => p + t;

                                  internal static Vector128<Pascal> Sum128(Vector128<Pascal> p, Vector128<Kelvin> t) => p - t;
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.Multiple(() =>
        {
            Assert.That(
                diagnostics.Select(diagnostic => diagnostic.Id),
                Is.EquivalentTo(new[] { "QNT001", "QNT001" }));
            Assert.That(
                diagnostics.Select(diagnostic => diagnostic.GetMessage()),
                Does.Contain("Operator '+' cannot combine 'length^-1 mass^1 time^-2' and 'temperature'"));
        });
    }

    [Test]
    public async Task VectorArithmeticDerivesLaneQuantities()
    {
        const string source = """
                              using System.Numerics;

                              internal static class Lanes
                              {
                                  internal static Vector<Pascal> Pressure(
                                      Vector<Mole> moles, Vector<Kelvin> temperature, PascalPerMoleKelvin factor)
                                  {
                                      Vector<Pascal> valid = moles * temperature * new Vector<float>(factor);
                                      Vector<Pascal> wrong = moles * temperature;
                                      return valid;
                                  }

                                  internal static Vector<Metre> Negated(Vector<Metre> d) => -d;
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        var diagnostic = diagnostics.Single();
        Assert.That(
            diagnostic.GetMessage(),
            Is.EqualTo("Cannot assign 'amount^1 temperature^1' to 'length^-1 mass^1 time^-2'"));
    }

    [Test]
    public async Task TypedArrayAndSpanLoadsKeepTheElementQuantity()
    {
        const string source = """
                              using System;
                              using System.Numerics;
                              using System.Runtime.InteropServices;
                              using System.Runtime.Intrinsics;

                              internal static class Lanes
                              {
                                  internal static void Load(Pascal[] p, Kelvin[] t, ReadOnlySpan<Kelvin> span, int i)
                                  {
                                      _ = new Vector<float>(p) + new Vector<float>(t);
                                      Vector<Kelvin> fromIndex = new Vector<float>(p, i);
                                      Vector<Pascal> fromSpan = new Vector<float>(span);
                                      Vector<Kelvin> fromRef = Vector.LoadUnsafe(ref p[i]);
                                      Vector<Kelvin> fromBase =
                                          Vector.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(p), (nuint)i);
                                      Vector128<Pascal> fromCreate = Vector128.Create(span);
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(
            diagnostics.Select(diagnostic => diagnostic.Id),
            Is.EquivalentTo(new[] { "QNT001", "QNT002", "QNT002", "QNT002", "QNT002", "QNT002" }));
    }

    [Test]
    public async Task TypedArrayLoadsWithMatchingQuantitiesPass()
    {
        const string source = """
                              using System.Numerics;
                              using System.Runtime.InteropServices;

                              internal static class Lanes
                              {
                                  internal static Vector<Pascal> Load(Pascal[] a, Pascal[] b, int i)
                                  {
                                      Vector<Pascal> fromRef = Vector.LoadUnsafe(ref a[i]);
                                      Vector<Pascal> fromBase =
                                          Vector.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(b), (nuint)i);
                                      Vector<Pascal> fromCast = MemoryMarshal.Cast<Pascal, Vector<Pascal>>(a)[0];
                                      return new Vector<float>(a, i) + new Vector<float>(b, i) + fromRef + fromBase + fromCast;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task BroadcastLiteralsAdoptTheLaneQuantity()
    {
        const string source = """
                              using System.Numerics;

                              internal static class Lanes
                              {
                                  internal static Vector<Pascal> Clamp(Vector<Pascal> p)
                                  {
                                      Vector<float> zero = Vector<float>.Zero;
                                      Vector<float> half = new Vector<float>(0.5f);
                                      Vector<Pascal> scaled = p * new Vector<float>(0.5f) + Vector<float>.One;
                                      Vector<Pascal> shifted = Vector<float>.Zero + p - new Vector<float>(1f);
                                      Vector<Pascal> floor = Vector.Max(p, Vector<float>.Zero);
                                      Vector<int> positive = Vector.GreaterThan(p, zero) & Vector.LessThan(p, half);
                                      Vector<Pascal> selected = Vector.ConditionalSelect(positive, zero, p);
                                      Vector<Pascal> inverse = Vector<float>.One / (Vector<float>.One / p);
                                      return scaled + shifted + floor + selected + inverse;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task BroadcastOfAScalarQuantityKeepsIt()
    {
        const string source = """
                              using System.Numerics;

                              internal static class Lanes
                              {
                                  internal static void Broadcast(Pascal pressure, float raw)
                                  {
                                      Vector<Pascal> valid = new Vector<float>(pressure);
                                      Vector<Kelvin> wrong = new Vector<float>(pressure);
                                      Vector<Kelvin> claimed = new Vector<float>(raw);
                                      var inferred = new Vector<float>(pressure);
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(
            diagnostics.Select(SpanText),
            Is.EquivalentTo(new[] { "new Vector<float>(pressure)", "new Vector<float>(pressure)" }));
    }

    [Test]
    public async Task VectorHelpersCheckTheirOperands()
    {
        const string source = """
                              using System.Numerics;

                              internal static class Lanes
                              {
                                  internal static void Helpers(
                                      Vector<Pascal> p, Vector<Kelvin> t, Vector<Metre> d, Vector<Second> s, Vector<int> mask)
                                  {
                                      _ = Vector.Min(p, t);
                                      _ = Vector.ConditionalSelect(mask, p, t);
                                      _ = Vector.LessThan(p, t);
                                      _ = Vector.FusedMultiplyAdd(d, d, s);
                                      Vector<Metre> fma = Vector.FusedMultiplyAdd(d, new Vector<float>(2f), d);
                                      Vector<Metre> abs = Vector.Abs(d);
                                      Vector<Second> wrongAbs = Vector.Abs(d);
                                      Vector<Metre> copied = Vector.CopySign(d, s);
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.Multiple(() =>
        {
            Assert.That(
                diagnostics.Select(diagnostic => diagnostic.Id),
                Is.EquivalentTo(new[] { "QNT003", "QNT003", "QNT003", "QNT003", "QNT002" }));
            Assert.That(
                diagnostics.Select(diagnostic => diagnostic.GetMessage()),
                Does.Contain(
                    "Argument has quantity 'temperature', but parameter 'right' requires 'length^-1 mass^1 time^-2'"));
        });
    }

    [Test]
    public async Task ReductionsAndLaneAccessUseTheLaneQuantity()
    {
        const string source = """
                              using System.Numerics;

                              internal static class Lanes
                              {
                                  internal static void Reduce(Vector<Metre> d, Vector<Second> s, Vector<Metre>[] batches)
                                  {
                                      Metre total = Vector.Sum(d);
                                      Metre first = d[0];
                                      Metre combined = (d + d)[1];
                                      Second wrong = d[0];
                                      Second dot = Vector.Dot(d, s);
                                      Vector<Second> element = batches[0];
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(
            diagnostics.Select(SpanText),
            Is.EquivalentTo(new[] { "d[0]", "Vector.Dot(d, s)", "batches[0]" }));
    }

    [Test]
    public async Task MaskingKeepsTheValueQuantity()
    {
        const string source = """
                              using System.Numerics;

                              internal static class Lanes
                              {
                                  internal static void Mask(Vector<Pascal> p, Vector<int> keep)
                                  {
                                      Vector<float> bits = Vector.AsVectorSingle(keep);
                                      Vector<Pascal> masked = Vector.BitwiseAnd(p, bits);
                                      Vector<Pascal> cleared = Vector.AndNot(p, bits);
                                      Vector<Pascal> operatorMasked = p & bits;
                                      Vector<Kelvin> wrong = Vector.BitwiseAnd(p, bits);
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(
            diagnostics.Select(SpanText),
            Is.EquivalentTo(new[] { "Vector.BitwiseAnd(p, bits)" }));
    }

    [Test]
    public async Task StoresCheckTheDestinationElementQuantity()
    {
        const string source = """
                              using System;
                              using System.Numerics;

                              internal static class Lanes
                              {
                                  internal static void Store(
                                      Vector<Pascal> p, Pascal[] pressures, Kelvin[] temperatures, float[] raw, Span<Kelvin> span)
                                  {
                                      p.CopyTo(pressures, 0);
                                      Vector.StoreUnsafe(p, ref pressures[0]);
                                      p.CopyTo(temperatures);
                                      p.CopyTo(span);
                                      p.StoreUnsafe(ref temperatures[0], 4);
                                      p.CopyTo(raw);
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.Multiple(() =>
        {
            Assert.That(
                diagnostics.Select(SpanText),
                Is.EquivalentTo(new[]
                {
                    "p.CopyTo(temperatures)", "p.CopyTo(span)", "p.StoreUnsafe(ref temperatures[0], 4)", "p.CopyTo(raw)"
                }));
            Assert.That(
                diagnostics.Single(diagnostic => SpanText(diagnostic) == "p.CopyTo(raw)").GetMessage(),
                Is.EqualTo("Cannot assign 'length^-1 mass^1 time^-2' to 'unannotated scalar'"));
        });
    }

    [Test]
    public async Task UnannotatedVectorTargetsCannotHoldAQuantity()
    {
        const string source = """
                              using System.Numerics;

                              internal static class Lanes
                              {
                                  internal static void Drop(Vector<Pascal> p, Vector<Pascal> q)
                                  {
                                      Vector<float> raw = p;
                                      Vector<float> ratio = p / q;
                                      Vector<float> total = Vector<float>.Zero;
                                      total += p;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(
            diagnostics.Select(SpanText),
            Is.EquivalentTo(new[] { "p", "total += p" }));
    }

    [Test]
    public async Task UnannotatedVectorCodeHasNoDiagnostics()
    {
        const string source = """
                              using System.Numerics;

                              internal static class Lanes
                              {
                                  internal static Vector<float> Kernel(float[] a, float[] b, float scale, int i)
                                  {
                                      var x = new Vector<float>(a, i);
                                      Vector<float> y = new Vector<float>(b, i) * new Vector<float>(scale);
                                      Vector<int> mask = Vector.GreaterThan(x, y);
                                      Vector<float> z = Vector.ConditionalSelect(mask, x, y) + Vector<float>.One;
                                      z.CopyTo(a, i);
                                      return Vector.SquareRoot(z * z) - Vector.Min(x, y);
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task CatalogAliasForAVectorTypeKeepsWorking()
    {
        const string source = """
                              using System.Numerics;

                              internal static class Lanes
                              {
                                  internal static void Read(PascalVector[] batches, Kelvin[] temperatures)
                                  {
                                      PascalVector valid = batches[0];
                                      Vector<Kelvin> wrong = batches[0];
                                      PascalVector loaded = new Vector<float>(temperatures);
                                  }
                              }
                              """;

        string catalog = Catalog +
                         Environment.NewLine +
                         "pressureVector | PascalVector | global::System.Numerics.Vector<float> | mass=1,length=-1,time=-2";

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, catalog);

        Assert.That(
            diagnostics.Select(SpanText),
            Is.EquivalentTo(new[] { "batches[0]", "new Vector<float>(temperatures)" }));
    }

    [Test]
    public async Task QuantityAttributeOnVectorApiCrossesAssemblies()
    {
        const string library = """
                               using System.Numerics;
                               using Magnitudo;

                               public static class Lib
                               {
                                   [return: Quantity("pressure")]
                                   public static Vector<float> Read() => default;

                                   public static void Write([Quantity("temperature")] Vector<float> value) { }
                               }
                               """;

        const string source = """
                              using System.Numerics;

                              internal static class Physics
                              {
                                  internal static void Run(Vector<Pascal> pressure, Vector<Kelvin> temperature)
                                  {
                                      Lib.Write(temperature);
                                      Lib.Write(pressure);
                                      _ = Lib.Read() + temperature;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(
            source,
            Catalog,
            CompileLibrary(library, Catalog));

        Assert.That(
            diagnostics.Select(diagnostic => diagnostic.Id),
            Is.EquivalentTo(new[] { "QNT003", "QNT001" }));
    }

    [Test]
    public async Task WidenChecksItsOutTargetsAgainstTheSource()
    {
        const string source = """
                              using System.Numerics;

                              internal static class Lanes
                              {
                                  internal static void Widen(Vector<Pascal> p, Vector<float> raw)
                                  {
                                      Vector.Widen(p, out Vector<Pascal64> low, out Vector<Pascal64> high);
                                      Vector.Widen(p, out Vector<Kelvin64> wrongLow, out Vector<Pascal64> rightHigh);
                                      Vector.Widen(p, out Vector<double> rawLow, out _);
                                      Vector.Widen(raw, out Vector<Kelvin64> claimedLow, out Vector<Kelvin64> claimedHigh);
                                  }
                              }
                              """;

        string catalog = Catalog +
                         Environment.NewLine +
                         "pressure64 | Pascal64 | global::System.Double | mass=1,length=-1,time=-2" +
                         Environment.NewLine +
                         "temperature64 | Kelvin64 | global::System.Double | temperature=1";

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, catalog);

        Assert.Multiple(() =>
        {
            Assert.That(
                diagnostics.Select(SpanText),
                Is.EquivalentTo(new[] { "Vector<Kelvin64> wrongLow", "Vector<double> rawLow" }));
            Assert.That(
                diagnostics.Select(diagnostic => diagnostic.GetMessage()),
                Is.EquivalentTo(new[]
                {
                    "Cannot assign 'length^-1 mass^1 time^-2' to 'temperature'",
                    "Cannot assign 'length^-1 mass^1 time^-2' to 'unannotated scalar'"
                }));
        });
    }

    [Test]
    public async Task VectorOperandsDoNotDependOnParameterNames()
    {
        // A local copy of Vector128 with renamed parameters takes the place of the BCL class (CS0436). The rules must
        // still find the condition, the sign source, the index, and the offset by position and type.
        const string source = """
                              using System.Runtime.Intrinsics;

                              namespace System.Runtime.Intrinsics
                              {
                                  internal static class Vector128
                                  {
                                      public static Vector128<T> ConditionalSelect<T>(
                                          Vector128<T> selector, Vector128<T> whenTrue, Vector128<T> whenFalse) => whenTrue;

                                      public static Vector128<T> CopySign<T>(Vector128<T> magnitude, Vector128<T> signSource) =>
                                          magnitude;

                                      public static T GetElement<T>(Vector128<T> lanes, int position) => default!;

                                      public static Vector128<T> LoadUnsafe<T>(ref T origin, nuint at) => default;

                                      public static Vector128<T> Min<T>(Vector128<T> first, Vector128<T> second) => first;

                                      public static int OnlyInTheLocalCopy() => 0;
                                  }
                              }

                              internal static class Lanes
                              {
                                  internal static void Run(Vector128<Pascal> p, Vector128<Kelvin> t, Pascal[] pressures)
                                  {
                                      _ = Vector128.OnlyInTheLocalCopy();
                                      Vector128<Pascal> selected = Vector128.ConditionalSelect(t, p, p);
                                      _ = Vector128.ConditionalSelect(t, p, t);
                                      Vector128<Pascal> signed = Vector128.CopySign(p, t);
                                      Pascal element = Vector128.GetElement(p, 3);
                                      Kelvin wrongElement = Vector128.GetElement(p, 3);
                                      Vector128<Kelvin> wrongLoad = Vector128.LoadUnsafe(ref pressures[0], 4);
                                      _ = Vector128.Min(p, t);
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(
            diagnostics.Select(diagnostic => diagnostic.Id + " " + SpanText(diagnostic)),
            Is.EquivalentTo(new[]
            {
                "QNT003 t",
                "QNT002 Vector128.GetElement(p, 3)",
                "QNT002 Vector128.LoadUnsafe(ref pressures[0], 4)",
                "QNT003 t"
            }));
    }

    [Test]
    public async Task IntegerLaneIndexesAreNotOperands()
    {
        const string source = """
                              using System.Numerics;
                              using System.Runtime.Intrinsics;

                              internal static class Lanes
                              {
                                  internal static void Run(Vector128<Cells> cells, Ticks[] ticks, Cells[] counts, Ticks at)
                                  {
                                      Cells element = Vector128.GetElement(cells, at);
                                      Vector<Cells> loaded = new Vector<int>(counts, at);
                                      Vector<Ticks> wrongLoad = new Vector<int>(counts, at);
                                      _ = Vector128.Create(element, element, element, ticks[0]);
                                  }
                              }
                              """;

        const string catalog = """
                               cells | Cells | global::System.Int32 | cell=1
                               ticks | Ticks | global::System.Int32 | tick=1
                               """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, catalog);

        Assert.That(
            diagnostics.Select(diagnostic => diagnostic.Id + " " + SpanText(diagnostic)),
            Is.EquivalentTo(new[] { "QNT002 new Vector<int>(counts, at)", "QNT003 ticks[0]" }));
    }

    [Test]
    public async Task HardwareIntrinsicsAreUnknown()
    {
        const string source = """
                              using System.Runtime.Intrinsics;
                              using System.Runtime.Intrinsics.X86;

                              internal static class Lanes
                              {
                                  internal static void Run(Vector256<Pascal> p, Vector256<Kelvin> t)
                                  {
                                      Vector256<Kelvin> sum = Avx.Add(p, t);
                                      Vector256<Pascal> product = Avx.Multiply(p, t);
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task SquareRootHalvesEvenExponents()
    {
        const string source = """
                              using System;

                              internal static class Physics
                              {
                                  internal static void Run(Metre d, Second t, Ratio r)
                                  {
                                      Metre side = MathF.Sqrt(d * d);
                                      Metre side64 = (float)Math.Sqrt(d * d);
                                      Second wrong = MathF.Sqrt(d * d);
                                      float raw = MathF.Sqrt(d * d);
                                      Metre perSecond = MathF.Sqrt(d * d / (t * t)) * t;
                                      Ratio ratio = MathF.Sqrt(r);
                                      float aspect = MathF.Sqrt(r);
                                      Metre diagonal = d * MathF.Sqrt(2f);
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.Multiple(() =>
        {
            Assert.That(
                diagnostics.Select(SpanText),
                Is.EquivalentTo(new[] { "MathF.Sqrt(d * d)", "MathF.Sqrt(d * d)" }));
            Assert.That(
                diagnostics.Select(diagnostic => diagnostic.GetMessage()),
                Is.EquivalentTo(new[]
                {
                    "Cannot assign 'length' to 'time'",
                    "Cannot assign 'length' to 'unannotated scalar'"
                }));
        });
    }

    [Test]
    public async Task SquareRootOfAnOddExponentIsUnknown()
    {
        const string source = """
                              using System;

                              internal static class Physics
                              {
                                  internal static void Run(Metre d)
                                  {
                                      Second claimed = MathF.Sqrt(d);
                                      float raw = MathF.Sqrt(d * d * d);
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(diagnostics, Is.Empty);
    }

    [TestCase("MathF.Cbrt(d * d * d)")]
    [TestCase("MathF.Pow(d * d, 0.5f)")]
    [TestCase("(float)Math.Pow(d * d, 0.5d)")]
    [TestCase("MathF.ReciprocalSqrtEstimate(d * d)")]
    public async Task OtherRootsStayUnknown(string expression)
    {
        string source = $$"""
                          using System;

                          internal static class Physics
                          {
                              internal static void Run(Metre d)
                              {
                                  Second claimed = {{expression}};
                                  float raw = {{expression}};
                              }
                          }
                          """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task VectorSquareRootHalvesEvenExponents()
    {
        const string source = """
                              using System.Numerics;
                              using System.Runtime.Intrinsics;

                              internal static class Lanes
                              {
                                  internal static void Run(Vector<Metre> d, Vector128<Metre> d128)
                                  {
                                      Vector<Metre> side = Vector.SquareRoot(d * d);
                                      Vector<Second> wrong = Vector.SquareRoot(d * d);
                                      Vector128<Metre> side128 = Vector128.Sqrt(d128 * d128);
                                      Vector128<Second> wrong128 = Vector128.Sqrt(d128 * d128);
                                      Vector<Second> odd = Vector.SquareRoot(d);
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(
            diagnostics.Select(SpanText),
            Is.EquivalentTo(new[] { "Vector.SquareRoot(d * d)", "Vector128.Sqrt(d128 * d128)" }));
    }

    private static async Task<ImmutableArray<Diagnostic>> Analyze(
        string source, string catalog,
        params MetadataReference[] references)
    {
        var additionalText = new InMemoryAdditionalText("Test.magnitudo", catalog);
        var compilation = GenerateAliases(
            CreateCompilation(source, "AnalyzerTest", references),
            catalog,
            out ImmutableArray<Diagnostic> generatorDiagnostics);

        Assert.That(generatorDiagnostics, Is.Empty);
        Assert.That(
            compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
            Is.Empty);

        var options = new AnalyzerOptions(ImmutableArray.Create<AdditionalText>(additionalText));
        var analyzers = ImmutableArray.Create<DiagnosticAnalyzer>(new DimensionalAnalysisAnalyzer());
        return await compilation.WithAnalyzers(analyzers, options).GetAnalyzerDiagnosticsAsync();
    }

    private static Compilation GenerateAliases(
        CSharpCompilation compilation, string catalog,
        out ImmutableArray<Diagnostic> diagnostics)
    {
        var additionalText = new InMemoryAdditionalText("Test.magnitudo", catalog);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new QuantityAliasGenerator().AsSourceGenerator()],
            [additionalText],
            (CSharpParseOptions)compilation.SyntaxTrees.Single().Options);

        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out diagnostics);
        return output;
    }

    private static string GeneratedSource(Compilation compilation)
    {
        return string.Concat(
            compilation.SyntaxTrees
                .Where(tree => tree.FilePath.Contains("QuantityAliases"))
                .Select(tree => tree.ToString()));
    }

    private static ImmutableArray<Diagnostic> ParseCatalog(string catalog)
    {
        GenerateAliases(CreateCompilation("internal static class C { }"), catalog, out var diagnostics);
        return diagnostics;
    }

    private static string SpanText(Diagnostic diagnostic)
    {
        return diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan);
    }

    private static MetadataReference CompileLibrary(string source, string catalog)
    {
        var compilation = GenerateAliases(CreateCompilation(source, "Library"), catalog, out _);
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.That(result.Success, Is.True, string.Join(Environment.NewLine, result.Diagnostics));
        return MetadataReference.CreateFromImage(stream.ToArray());
    }

    private static CSharpCompilation CreateCompilation(
        string source, string assemblyName = "AnalyzerTest",
        IEnumerable<MetadataReference>? additionalReferences = null,
        LanguageVersion languageVersion = LanguageVersion.Latest,
        DocumentationMode documentationMode = DocumentationMode.Parse)
    {
        string[] trustedAssemblies = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator);

        IEnumerable<MetadataReference> references = trustedAssemblies
            .Append(typeof(QuantityAttribute).Assembly.Location)
            .Distinct(StringComparer.Ordinal)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .Concat(additionalReferences ?? []);

        return CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(languageVersion, documentationMode))],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private sealed class InMemoryAdditionalText : AdditionalText
    {
        private readonly SourceText _text;

        internal InMemoryAdditionalText(string path, string text)
        {
            Path = path;
            _text = SourceText.From(text);
        }

        public override string Path { get; }

        public override SourceText GetText(CancellationToken cancellationToken = default)
        {
            return _text;
        }
    }
}