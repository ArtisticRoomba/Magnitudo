using System.Collections.Immutable;
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

    private static async Task<ImmutableArray<Diagnostic>> Analyze(string source, string catalog)
    {
        var additionalText = new InMemoryAdditionalText("Test.magnitudo", catalog);
        var compilation = GenerateAliases(
            CreateCompilation(source),
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

    private static CSharpCompilation CreateCompilation(string source)
    {
        string[] trustedAssemblies = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator);

        IEnumerable<MetadataReference> references = trustedAssemblies
            .Append(typeof(QuantityAttribute).Assembly.Location)
            .Distinct(StringComparer.Ordinal)
            .Select(path => MetadataReference.CreateFromFile(path));

        return CSharpCompilation.Create(
            "AnalyzerTest",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))],
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