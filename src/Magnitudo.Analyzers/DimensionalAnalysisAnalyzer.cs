using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Magnitudo.Analyzers;

/// <summary>
///     Reports QNT001 to QNT004 where two known quantities disagree or a known quantity loses its dimension.
/// </summary>
/// <remarks>
///     The analyzer does nothing when the compilation has no catalog quantities. It reads quantities from
///     <c>[Quantity]</c> attributes and from alias names in the declaring syntax, because the compiler erases aliases
///     from symbols.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DimensionalAnalysisAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(
            Diagnostics.InvalidCatalog,
            Diagnostics.IncompatibleOperands,
            Diagnostics.IncompatibleAssignment,
            Diagnostics.IncompatibleArgument,
            Diagnostics.IncompatibleReturn);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(startContext =>
        {
            var catalog = UnitCatalog.Parse(
                startContext.Options.AdditionalFiles,
                startContext.CancellationToken);

            if (catalog.Definitions.Length == 0)
                return;

            var analysis = new QuantityAnalysis(startContext.Compilation, catalog);
            startContext.RegisterOperationAction(analysis.AnalyzeBinary, OperationKind.Binary);
            startContext.RegisterOperationAction(analysis.AnalyzeAssignment, OperationKind.SimpleAssignment);
            startContext.RegisterOperationAction(
                analysis.AnalyzeCompoundAssignment,
                OperationKind.CompoundAssignment);

            startContext.RegisterOperationAction(analysis.AnalyzeVariable, OperationKind.VariableDeclarator);
            startContext.RegisterOperationAction(analysis.AnalyzeArgument, OperationKind.Argument);
            startContext.RegisterOperationAction(analysis.AnalyzeReturn, OperationKind.Return);
            startContext.RegisterOperationAction(analysis.AnalyzeInvocation, OperationKind.Invocation);
        });
    }

    /// <summary>
    ///     Holds the per-compilation catalog and the inference rules for all registered operation actions.
    /// </summary>
    private sealed class QuantityAnalysis
    {
        private const string QuantityAttributeName = "Magnitudo.QuantityAttribute";
        private const string ElementQuantityAttributeName = "Magnitudo.ElementQuantityAttribute";
        private readonly UnitCatalog _catalog;

        private readonly Compilation _compilation;

        internal QuantityAnalysis(Compilation compilation, UnitCatalog catalog)
        {
            _compilation = compilation;
            _catalog = catalog;
        }

        internal void AnalyzeBinary(OperationAnalysisContext context)
        {
            var operation = (IBinaryOperation)context.Operation;
            if (!RequiresEqualOperands(operation.OperatorKind) || IsStringConcatenation(operation))
                return;

            var left = Infer(operation.LeftOperand);
            var right = Infer(operation.RightOperand);
            if (AreIncompatible(left, right))
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        Diagnostics.IncompatibleOperands,
                        operation.Syntax.GetLocation(),
                        GetOperatorText(operation),
                        left,
                        right));
            }
        }

        internal void AnalyzeAssignment(OperationAnalysisContext context)
        {
            var operation = (ISimpleAssignmentOperation)context.Operation;
            if (operation.Target is IDiscardOperation)
                return;

            ReportAssignmentIfNeeded(context, operation.Target, operation.Value, operation.Syntax.GetLocation());
        }

        internal void AnalyzeCompoundAssignment(OperationAnalysisContext context)
        {
            var operation = (ICompoundAssignmentOperation)context.Operation;
            var target = Infer(operation.Target);
            var value = Infer(operation.Value);
            if (!target.IsKnown || !value.IsKnown)
                return;

            var result = operation.OperatorKind switch
            {
                BinaryOperatorKind.Add or BinaryOperatorKind.Subtract => value,
                BinaryOperatorKind.Multiply => target.Multiply(value),
                BinaryOperatorKind.Divide => target.Divide(value),
                _ => QuantityValue.Unknown
            };

            if (IsIncompatibleAssignment(target, result))
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        Diagnostics.IncompatibleAssignment,
                        operation.Syntax.GetLocation(),
                        result,
                        target));
            }
        }

        internal void AnalyzeVariable(OperationAnalysisContext context)
        {
            var operation = (IVariableDeclaratorOperation)context.Operation;
            if (operation.Initializer is null)
                return;

            var target = GetSymbolQuantity(operation.Symbol);
            var value = Infer(operation.Initializer.Value);
            if (IsIncompatibleAssignment(target, value))
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        Diagnostics.IncompatibleAssignment,
                        operation.Initializer.Value.Syntax.GetLocation(),
                        value,
                        target));
            }
        }

        internal void AnalyzeArgument(OperationAnalysisContext context)
        {
            var operation = (IArgumentOperation)context.Operation;
            if (operation.Parameter is null)
                return;

            var declared = GetSymbolQuantity(operation.Parameter);

            switch (operation.Parameter.RefKind)
            {
                case RefKind.Out:
                {
                    if (operation.Value is IDiscardOperation)
                        return;

                    // Data flows from the parameter to the argument, so the caller's variable is the target
                    // and the parameter's quantity is the value.
                    var target = Infer(operation.Value);
                    if (IsIncompatibleAssignment(target, declared))
                    {
                        context.ReportDiagnostic(
                            Diagnostic.Create(
                                Diagnostics.IncompatibleArgument,
                                operation.Value.Syntax.GetLocation(),
                                target,
                                operation.Parameter.Name,
                                declared));
                    }

                    return;
                }
                case RefKind.Ref:
                {
                    // An unannotated ref parameter accepts anything, so Swap<T>(ref T, ref T) works.
                    // A declared ref parameter needs a matching quantity.
                    var target = Infer(operation.Value);
                    if (IsIncompatibleArgument(declared, target))
                    {
                        context.ReportDiagnostic(
                            Diagnostic.Create(
                                Diagnostics.IncompatibleArgument,
                                operation.Value.Syntax.GetLocation(),
                                target,
                                operation.Parameter.Name,
                                declared));
                    }

                    return;
                }
                default:
                {
                    var actual = Infer(operation.Value);
                    if (IsIncompatibleArgument(declared, actual))
                    {
                        context.ReportDiagnostic(
                            Diagnostic.Create(
                                Diagnostics.IncompatibleArgument,
                                operation.Value.Syntax.GetLocation(),
                                actual,
                                operation.Parameter.Name,
                                declared));
                    }

                    return;
                }
            }
        }

        internal void AnalyzeReturn(OperationAnalysisContext context)
        {
            var operation = (IReturnOperation)context.Operation;
            if (operation.ReturnedValue is null)
                return;

            // ContainingSymbol is the enclosing member. A return inside a lambda or local function belongs
            // to that inner function, so the code looks for it first.
            var function = GetEnclosingFunction(operation, out bool isLambda) ??
                           context.ContainingSymbol as IMethodSymbol;

            if (function is null)
                return;

            var expected = GetReturnStatementQuantity(function);

            // A lambda usually takes its return type from a delegate type, where the alias is already
            // erased. The analyzer checks only lambdas that declare a quantity.
            if (isLambda && !expected.IsKnown)
                return;

            var actual = Infer(operation.ReturnedValue);
            if (IsIncompatibleAssignment(expected, actual))
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        Diagnostics.IncompatibleReturn,
                        operation.ReturnedValue.Syntax.GetLocation(),
                        actual,
                        expected));
            }
        }

        internal void AnalyzeInvocation(OperationAnalysisContext context)
        {
            var operation = (IInvocationOperation)context.Operation;

            // CopySign(x, y) uses only the sign of y, so y can have any quantity.
            if (!IsQuantityPreservingMath(operation.TargetMethod) || IsCopySign(operation.TargetMethod))
                return;

            var expected = QuantityValue.Unknown;
            foreach (var argument in operation.Arguments)
            {
                var actual = Infer(argument.Value);
                if (!actual.IsKnown)
                    continue;

                if (!expected.IsKnown)
                {
                    expected = actual;
                    continue;
                }

                if (AreIncompatible(expected, actual))
                {
                    context.ReportDiagnostic(
                        Diagnostic.Create(
                            Diagnostics.IncompatibleArgument,
                            argument.Value.Syntax.GetLocation(),
                            actual,
                            argument.Parameter?.Name ?? "value",
                            expected));
                }
            }
        }

        private void ReportAssignmentIfNeeded(
            OperationAnalysisContext context, IOperation targetOperation,
            IOperation valueOperation, Location location)
        {
            var target = Infer(targetOperation);
            var value = Infer(valueOperation);
            if (IsIncompatibleAssignment(target, value))
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        Diagnostics.IncompatibleAssignment,
                        location,
                        value,
                        target));
            }
        }

        /// <summary>
        ///     Computes the quantity state of an expression.
        /// </summary>
        /// <remarks>
        ///     Anything the analyzer cannot follow is <see cref="QuantityValue.Unknown" />. Inference never reports
        ///     errors itself.
        /// </remarks>
        private QuantityValue Infer(IOperation? operation)
        {
            if (operation is null)
                return QuantityValue.Unknown;

            switch (operation)
            {
                case IConversionOperation conversion:
                    return Infer(conversion.Operand);
                case IParenthesizedOperation parenthesized:
                    return Infer(parenthesized.Operand);
                case IUnaryOperation unary when unary.OperatorKind is UnaryOperatorKind.Plus or UnaryOperatorKind.Minus:
                    return Infer(unary.Operand);
                case ILocalReferenceOperation local:
                    return GetSymbolQuantity(local.Local);
                case IParameterReferenceOperation parameter:
                    return GetSymbolQuantity(parameter.Parameter);
                case IFieldReferenceOperation field:
                    return GetSymbolQuantity(field.Field);
                case IPropertyReferenceOperation property when property.Property.IsIndexer:
                {
                    // An indexer that declares its own quantity takes precedence over the element type.
                    var declared = GetSymbolQuantity(property.Property);
                    return declared.IsKnown ? declared : GetElementQuantity(property.Instance);
                }
                case IPropertyReferenceOperation property:
                    return GetSymbolQuantity(property.Property);
                case IArrayElementReferenceOperation arrayElement:
                    return GetElementQuantity(arrayElement.ArrayReference);
                case IInvocationOperation invocation:
                {
                    var declared = GetMethodReturnQuantity(invocation.TargetMethod);
                    if (declared.IsKnown)
                        return declared;

                    if (IsCopySign(invocation.TargetMethod))
                        return Infer(GetArgument(invocation, 0));

                    if (IsQuantityPreservingMath(invocation.TargetMethod))
                    {
                        return invocation.Arguments.Select(argument => Infer(argument.Value))
                            .FirstOrDefault(quantity => quantity.IsKnown);
                    }

                    return QuantityValue.Unknown;
                }
                case IAwaitOperation { Operation: IInvocationOperation awaited }:
                    return GetAwaitedQuantity(awaited.TargetMethod);
                case IBinaryOperation binary:
                    return InferBinary(binary);
                case IConditionalOperation conditional:
                    return Merge(Infer(conditional.WhenTrue), Infer(conditional.WhenFalse));
                case ICoalesceOperation coalesce:
                    return Merge(Infer(coalesce.Value), Infer(coalesce.WhenNull));
                case IObjectOrCollectionInitializerOperation:
                case ILiteralOperation:
                case IDefaultValueOperation:
                    return QuantityValue.LiteralScalar;
                case IDeclarationExpressionOperation declaration:
                    return Infer(declaration.Expression);
                case IDiscardOperation:
                    return QuantityValue.LiteralScalar;
                default:
                    return QuantityValue.Unknown;
            }
        }

        private QuantityValue InferBinary(IBinaryOperation binary)
        {
            if (IsStringConcatenation(binary))
                return QuantityValue.Unknown;

            var left = Infer(binary.LeftOperand);
            var right = Infer(binary.RightOperand);
            return binary.OperatorKind switch
            {
                BinaryOperatorKind.Add or BinaryOperatorKind.Subtract => Merge(left, right),
                BinaryOperatorKind.Multiply => left.Multiply(right),
                BinaryOperatorKind.Divide => left.Divide(right),
                _ => QuantityValue.Unknown
            };
        }

        private QuantityValue GetElementQuantity(IOperation? operation)
        {
            operation = Unwrap(operation);
            if (operation is IInvocationOperation invocation)
            {
                var returned = GetAttributeQuantity(
                    invocation.TargetMethod.GetReturnTypeAttributes(),
                    ElementQuantityAttributeName);

                return returned.IsKnown ? returned : GetSyntaxQuantity(invocation.TargetMethod, true);
            }

            ISymbol? symbol = operation switch
            {
                IFieldReferenceOperation field => field.Field,
                IPropertyReferenceOperation property => property.Property,
                IParameterReferenceOperation parameter => parameter.Parameter,
                ILocalReferenceOperation local => local.Local,
                _ => null
            };

            if (symbol is null)
                return QuantityValue.Unknown;

            var attributed = GetAttributeQuantity(symbol, ElementQuantityAttributeName);
            return attributed.IsKnown ? attributed : GetSyntaxQuantity(symbol, true);
        }

        private QuantityValue GetSymbolQuantity(ISymbol symbol)
        {
            var attributed = GetAttributeQuantity(symbol, QuantityAttributeName);
            return attributed.IsKnown ? attributed : GetSyntaxQuantity(symbol, false);
        }

        private QuantityValue GetMethodReturnQuantity(IMethodSymbol method)
        {
            if (method.MethodKind == MethodKind.PropertyGet && method.AssociatedSymbol is IPropertySymbol property)
                return GetSymbolQuantity(property);

            var attributed = GetAttributeQuantity(method.GetReturnTypeAttributes(), QuantityAttributeName);
            return attributed.IsKnown ? attributed : GetSyntaxQuantity(method, false);
        }

        private QuantityValue GetReturnStatementQuantity(IMethodSymbol method)
        {
            // In an async method, `return x;` produces the T of Task<T>, not the task itself.
            return method.IsAsync ? GetAwaitedQuantity(method) : GetMethodReturnQuantity(method);
        }

        private QuantityValue GetAwaitedQuantity(IMethodSymbol method)
        {
            var attributed = GetAttributeQuantity(method.GetReturnTypeAttributes(), QuantityAttributeName);
            return attributed.IsKnown ? attributed : GetSyntaxQuantity(method, true);
        }

        private static IMethodSymbol? GetEnclosingFunction(IOperation operation, out bool isLambda)
        {
            for (var parent = operation.Parent; parent is not null; parent = parent.Parent)
            {
                switch (parent)
                {
                    case IAnonymousFunctionOperation lambda:
                        isLambda = true;
                        return lambda.Symbol;
                    case ILocalFunctionOperation localFunction:
                        isLambda = false;
                        return localFunction.Symbol;
                }
            }

            isLambda = false;
            return null;
        }

        private QuantityValue GetAttributeQuantity(ISymbol symbol, string attributeName)
        {
            return GetAttributeQuantity(symbol.GetAttributes(), attributeName);
        }

        private QuantityValue GetAttributeQuantity(ImmutableArray<AttributeData> attributes, string attributeName)
        {
            foreach (var attribute in attributes)
            {
                if (attribute.AttributeClass?.ToDisplayString() != attributeName ||
                    attribute.ConstructorArguments.Length != 1 ||
                    attribute.ConstructorArguments[0].Value is not string id ||
                    !_catalog.TryGetId(id, out var definition))
                    continue;

                return QuantityValue.From(definition);
            }

            return QuantityValue.Unknown;
        }

        private QuantityValue GetSyntaxQuantity(ISymbol symbol, bool element)
        {
            foreach (var syntaxReference in symbol.DeclaringSyntaxReferences)
            {
                var syntax = syntaxReference.GetSyntax();
                var type = GetDeclaredType(syntax);
                if (type is null)
                    continue;

                if (element)
                    type = GetElementType(type);

                // `Metre?` and `ref Metre` have the same quantity as `Metre`.
                while (type is NullableTypeSyntax or RefTypeSyntax)
                    type = type is NullableTypeSyntax nullable ? nullable.ElementType : ((RefTypeSyntax)type).Type;

                if (type is null)
                    continue;

                var semanticModel = _compilation.GetSemanticModel(type.SyntaxTree);
                var alias = semanticModel.GetAliasInfo(type);
                string aliasName = alias?.Name ?? (type as IdentifierNameSyntax)?.Identifier.ValueText ?? string.Empty;
                if (_catalog.TryGetAlias(aliasName, out var definition))
                    return QuantityValue.From(definition);
            }

            return QuantityValue.Unknown;
        }

        private static TypeSyntax? GetDeclaredType(SyntaxNode syntax)
        {
            return syntax switch
            {
                ParameterSyntax parameter => parameter.Type,
                VariableDeclaratorSyntax variable => (variable.Parent as VariableDeclarationSyntax)?.Type,
                SingleVariableDesignationSyntax { Parent: DeclarationExpressionSyntax declaration } => declaration.Type,
                PropertyDeclarationSyntax property => property.Type,
                IndexerDeclarationSyntax indexer => indexer.Type,
                MethodDeclarationSyntax method => method.ReturnType,
                LocalFunctionStatementSyntax localFunction => localFunction.ReturnType,
                ParenthesizedLambdaExpressionSyntax lambda => lambda.ReturnType,
                OperatorDeclarationSyntax @operator => @operator.ReturnType,
                ConversionOperatorDeclarationSyntax conversion => conversion.Type,
                _ => null
            };
        }

        private static TypeSyntax? GetElementType(TypeSyntax type)
        {
            return type switch
            {
                ArrayTypeSyntax array => array.ElementType,
                GenericNameSyntax generic when generic.TypeArgumentList.Arguments.Count > 0 =>
                    generic.TypeArgumentList.Arguments[generic.TypeArgumentList.Arguments.Count - 1],
                QualifiedNameSyntax { Right: GenericNameSyntax generic } when
                    generic.TypeArgumentList.Arguments.Count > 0 =>
                    generic.TypeArgumentList.Arguments[generic.TypeArgumentList.Arguments.Count - 1],
                _ => null
            };
        }

        private static IOperation? Unwrap(IOperation? operation)
        {
            while (operation is IConversionOperation conversion)
                operation = conversion.Operand;

            return operation;
        }

        private static bool RequiresEqualOperands(BinaryOperatorKind kind)
        {
            return kind is
                BinaryOperatorKind.Add
                or BinaryOperatorKind.Subtract
                or BinaryOperatorKind.Equals
                or BinaryOperatorKind.NotEquals
                or BinaryOperatorKind.LessThan
                or BinaryOperatorKind.LessThanOrEqual
                or BinaryOperatorKind.GreaterThan
                or BinaryOperatorKind.GreaterThanOrEqual;
        }

        private static bool IsCopySign(IMethodSymbol method)
        {
            return method.Name == "CopySign" && IsQuantityPreservingMath(method);
        }

        private static IOperation? GetArgument(IInvocationOperation invocation, int ordinal)
        {
            return invocation.Arguments.FirstOrDefault(argument => argument.Parameter?.Ordinal == ordinal)?.Value;
        }

        private static bool IsStringConcatenation(IBinaryOperation operation)
        {
            return operation.OperatorKind == BinaryOperatorKind.Add &&
                   operation.Type?.SpecialType == SpecialType.System_String;
        }

        private static bool IsQuantityPreservingMath(IMethodSymbol method)
        {
            string containingType = method.ContainingType.ToDisplayString();
            if (containingType is not ("System.Math" or "System.MathF"))
                return false;

            return method.Name is "Abs" or "Clamp" or "CopySign" or "Max" or "MaxMagnitude" or "Min" or "MinMagnitude";
        }

        private static string GetOperatorText(IBinaryOperation operation)
        {
            if (operation.Syntax is BinaryExpressionSyntax binary)
                return binary.OperatorToken.Text;

            return operation.OperatorKind.ToString();
        }

        private static bool AreIncompatible(QuantityValue left, QuantityValue right)
        {
            return left.IsKnown && right.IsKnown && !left.Dimensions!.Equals(right.Dimensions);
        }

        /// <summary>
        ///     Decides whether <paramref name="value" /> can flow into <paramref name="target" /> by assignment,
        ///     initializer, or return.
        /// </summary>
        private static bool IsIncompatibleAssignment(QuantityValue target, QuantityValue value)
        {
            if (target.IsKnown && value.IsKnown)
                return !target.Dimensions!.Equals(value.Dimensions);

            // A literal or unannotated value can claim a unit: `Kelvin t = raw;`.
            if (target.IsKnown)
                return false;

            // An unannotated target would drop the unit of a dimensioned value. A dimensionless value has
            // no unit to lose, so `float aspect = width / height;` is allowed.
            return value.IsKnown && !value.Dimensions!.IsScalar();
        }

        /// <summary>
        ///     Decides whether <paramref name="actual" /> can be passed to a parameter declared as
        ///     <paramref name="expected" />.
        /// </summary>
        /// <remarks>
        ///     This is stricter than <see cref="IsIncompatibleAssignment" />. An unannotated argument is an error
        ///     here, because a raw value at a call site is more likely a missing annotation than a deliberate claim.
        /// </remarks>
        private static bool IsIncompatibleArgument(QuantityValue expected, QuantityValue actual)
        {
            // An unannotated parameter accepts anything, so a Metre can go into MathF.Max(float, float).
            if (!expected.IsKnown)
                return false;

            // A literal such as `0f` has no dimension of its own. A dimensionless alias or a computed
            // dimensionless value is a known quantity, so it is checked like any other.
            if (actual.IsLiteralScalar)
                return false;

            return !actual.IsKnown || !expected.Dimensions!.Equals(actual.Dimensions);
        }

        /// <summary>
        ///     Combines the branches of a sum, difference, conditional, or coalesce.
        /// </summary>
        /// <remarks>
        ///     A literal adopts the quantity of the other side. Unknown or mismatched sides give unknown.
        /// </remarks>
        private static QuantityValue Merge(QuantityValue left, QuantityValue right)
        {
            if (left.IsLiteralScalar)
                return right;

            if (right.IsLiteralScalar)
                return left;

            if (!left.IsKnown || !right.IsKnown)
                return QuantityValue.Unknown;

            return left.Dimensions!.Equals(right.Dimensions) ? left : QuantityValue.Unknown;
        }
    }

    /// <summary>
    ///     The state of an expression: known (has a dimension), literal, or unknown (unannotated).
    /// </summary>
    /// <remarks>
    ///     The default value is unknown. A literal has no dimension and adopts the dimension of whatever it meets.
    /// </remarks>
    private readonly struct QuantityValue
    {
        private QuantityValue(DimensionVector? dimensions, bool isLiteralScalar = false)
        {
            Dimensions = dimensions;
            IsLiteralScalar = dimensions is null && isLiteralScalar;
        }

        internal static QuantityValue Unknown => default;
        internal static QuantityValue LiteralScalar => new(null, true);
        internal DimensionVector? Dimensions { get; }
        internal bool IsKnown => Dimensions is not null;
        internal bool IsLiteralScalar { get; }

        internal static QuantityValue From(QuantityDefinition definition)
        {
            return new QuantityValue(definition.Dimensions);
        }

        /// <summary>
        ///     Adds the exponents of both operands. A literal operand leaves the other operand unchanged.
        /// </summary>
        internal QuantityValue Multiply(QuantityValue other)
        {
            if (IsLiteralScalar)
                return other;

            if (other.IsLiteralScalar)
                return this;

            if (!IsKnown || !other.IsKnown)
                return Unknown;

            return new QuantityValue(Dimensions!.Multiply(other.Dimensions!));
        }

        /// <summary>
        ///     Subtracts the exponents of <paramref name="other" />. A literal numerator inverts the denominator.
        /// </summary>
        internal QuantityValue Divide(QuantityValue other)
        {
            if (IsLiteralScalar)
                return other.IsKnown ? new QuantityValue(other.Dimensions!.Invert()) : other;

            if (other.IsLiteralScalar)
                return this;

            if (!IsKnown || !other.IsKnown)
                return Unknown;

            return new QuantityValue(Dimensions!.Divide(other.Dimensions!));
        }

        public override string ToString()
        {
            return Dimensions?.ToString() ??
                   (IsLiteralScalar ? "numeric literal" : "unannotated scalar");
        }
    }
}