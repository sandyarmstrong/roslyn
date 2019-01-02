﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.CodeAnalysis.Collections;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.PooledObjects;

namespace Microsoft.CodeAnalysis.CSharp
{
    /// <summary>
    /// This portion of the binder converts a AnonymousObjectCreationExpressionSyntax into 
    /// a bound anonymous object creation node
    /// </summary>
    internal partial class Binder
    {
        private BoundExpression BindAnonymousObjectCreation(AnonymousObjectCreationExpressionSyntax node, DiagnosticBag diagnostics)
        {
            //  prepare
            var initializers = node.Initializers;
            int fieldCount = initializers.Count;
            bool hasError = false;

            //  bind field initializers
            BoundExpression[] boundExpressions = new BoundExpression[fieldCount];
            AnonymousTypeField[] fields = new AnonymousTypeField[fieldCount];
            CSharpSyntaxNode[] fieldSyntaxNodes = new CSharpSyntaxNode[fieldCount];

            // WARNING: Note that SemanticModel.GetDeclaredSymbol for field initializer node relies on 
            //          the fact that the order of properties in anonymous type template corresponds 
            //          1-to-1 to the appropriate filed initializer syntax nodes; This means such 
            //          correspondence must be preserved all the time including erroneous scenarios

            // set of names already used
            var uniqueFieldNames = PooledHashSet<string>.GetInstance();

            for (int i = 0; i < fieldCount; i++)
            {
                AnonymousObjectMemberDeclaratorSyntax fieldInitializer = initializers[i];
                NameEqualsSyntax nameEquals = fieldInitializer.NameEquals;
                ExpressionSyntax expression = fieldInitializer.Expression;

                SyntaxToken nameToken = default(SyntaxToken);
                if (nameEquals != null)
                {
                    nameToken = nameEquals.Name.Identifier;
                }
                else
                {
                    if (!IsAnonymousTypeMemberExpression(expression))
                    {
                        hasError = true;
                        diagnostics.Add(ErrorCode.ERR_InvalidAnonymousTypeMemberDeclarator, expression.GetLocation());
                    }

                    nameToken = expression.ExtractAnonymousTypeMemberName();
                }

                hasError |= expression.HasErrors;
                boundExpressions[i] = this.BindValue(expression, diagnostics, BindValueKind.RValue);

                //  check the name to be unique
                string fieldName = null;
                if (nameToken.Kind() == SyntaxKind.IdentifierToken)
                {
                    fieldName = nameToken.ValueText;
                    if (!uniqueFieldNames.Add(fieldName))
                    {
                        //  name duplication
                        Error(diagnostics, ErrorCode.ERR_AnonymousTypeDuplicatePropertyName, fieldInitializer);
                        hasError = true;
                        fieldName = null;
                    }
                }
                else
                {
                    // there is something wrong with field's name
                    hasError = true;
                }

                //  calculate the expression's type and report errors if needed
                TypeSymbol fieldType = GetAnonymousTypeFieldType(boundExpressions[i], fieldInitializer, diagnostics, ref hasError);

                // build anonymous type field descriptor
                fieldSyntaxNodes[i] = (nameToken.Kind() == SyntaxKind.IdentifierToken) ? (CSharpSyntaxNode)nameToken.Parent : fieldInitializer;
                // https://github.com/dotnet/roslyn/issues/24018: Initial binding should set NullableAnnotation.Unknown
                NullableAnnotation nullableAnnotation;
                switch (((CSharpParseOptions)node.SyntaxTree?.Options)?.IsFeatureEnabled(MessageID.IDS_FeatureNullableReferenceTypes))
                {
                    case true:
                        nullableAnnotation = fieldType.IsReferenceType ? NullableAnnotation.Annotated : NullableAnnotation.NotAnnotated;
                        break;

                    case false:
                        nullableAnnotation = NullableAnnotation.NotAnnotated;
                        break;

                    default:
                        nullableAnnotation = NullableAnnotation.Unknown;
                        break;
                }

                fields[i] = new AnonymousTypeField(
                    fieldName == null ? "$" + i.ToString() : fieldName,
                    fieldSyntaxNodes[i].Location,
                    TypeSymbolWithAnnotations.Create(fieldType, nullableAnnotation));

                //  NOTE: ERR_InvalidAnonymousTypeMemberDeclarator (CS0746) would be generated by parser if needed
            }

            uniqueFieldNames.Free();

            //  Create anonymous type 
            AnonymousTypeManager manager = this.Compilation.AnonymousTypeManager;
            AnonymousTypeDescriptor descriptor = new AnonymousTypeDescriptor(fields.AsImmutableOrNull(), node.NewKeyword.GetLocation());
            NamedTypeSymbol anonymousType = manager.ConstructAnonymousTypeSymbol(descriptor);

            // declarators - bound nodes created for providing semantic info 
            // on anonymous type fields having explicitly specified name
            ArrayBuilder<BoundAnonymousPropertyDeclaration> declarators =
                ArrayBuilder<BoundAnonymousPropertyDeclaration>.GetInstance();
            for (int i = 0; i < fieldCount; i++)
            {
                NameEqualsSyntax explicitName = initializers[i].NameEquals;
                if (explicitName != null)
                {
                    AnonymousTypeField field = fields[i];
                    if (field.Name != null)
                    {
                        //  get property symbol and create a bound property declaration node
                        foreach (var symbol in anonymousType.GetMembers(field.Name))
                        {
                            if (symbol.Kind == SymbolKind.Property)
                            {
                                declarators.Add(new BoundAnonymousPropertyDeclaration(fieldSyntaxNodes[i], (PropertySymbol)symbol, field.Type.TypeSymbol));
                                break;
                            }
                        }
                    }
                }
            }

            // check if anonymous object creation is allowed in this context
            if (!this.IsAnonymousTypesAllowed())
            {
                Error(diagnostics, ErrorCode.ERR_AnonymousTypeNotAvailable, node.NewKeyword);
                hasError = true;
            }

            //  Finally create a bound node
            return new BoundAnonymousObjectCreationExpression(
                node,
                anonymousType.InstanceConstructors[0],
                boundExpressions.AsImmutableOrNull(),
                declarators.ToImmutableAndFree(),
                anonymousType,
                hasError);
        }

        private static bool IsAnonymousTypeMemberExpression(ExpressionSyntax expr)
        {
            while (true)
            {
                switch (expr.Kind())
                {
                    case SyntaxKind.QualifiedName:
                        expr = ((QualifiedNameSyntax)expr).Right;
                        continue;
                    case SyntaxKind.ConditionalAccessExpression:
                        expr = ((ConditionalAccessExpressionSyntax)expr).WhenNotNull;
                        if (expr.Kind() == SyntaxKind.MemberBindingExpression)
                        {
                            return true;
                        }

                        continue;
                    case SyntaxKind.IdentifierName:
                    case SyntaxKind.SimpleMemberAccessExpression:
                        return true;
                    default:
                        return false;
                }
            }
        }

        /// <summary>
        /// Actually, defines if an error ERR_AnonymousTypeNotAvailable is to be generated; 
        /// 
        /// Dev10 rules (which are based on BindingContext::InMethod()) are difficult to 
        /// reproduce, so this implementation checks both current symbol as well as syntax nodes.
        /// </summary>
        private bool IsAnonymousTypesAllowed()
        {
            var member = this.ContainingMemberOrLambda;
            if ((object)member == null)
            {
                return false;
            }

            switch (member.Kind)
            {
                case SymbolKind.Method:
                    return true;

                case SymbolKind.Field:
                    return !((FieldSymbol)member).IsConst;

                case SymbolKind.NamedType:
                    //  allow usage of anonymous types in script classes
                    return ((NamedTypeSymbol)member).IsScriptClass;
            }

            return false;
        }

        /// <summary>
        /// Returns the type to be used as a field type; generates errors in case the type is not
        /// supported for anonymous type fields.
        /// </summary>
        private TypeSymbol GetAnonymousTypeFieldType(BoundExpression expression, CSharpSyntaxNode errorSyntax, DiagnosticBag diagnostics, ref bool hasError)
        {
            object errorArg = null;
            TypeSymbol expressionType = expression.Type;

            if (!expression.HasAnyErrors)
            {
                if (expression.HasExpressionType())
                {
                    if (expressionType.SpecialType == SpecialType.System_Void)
                    {
                        errorArg = expressionType;
                        expressionType = CreateErrorType(SyntaxFacts.GetText(SyntaxKind.VoidKeyword));
                    }
                    else if (expressionType.IsUnsafe())
                    {
                        errorArg = expressionType;
                        // CONSIDER: we could use an explicit error type instead of the unsafe type.
                    }
                    else if (expressionType.IsRestrictedType())
                    {
                        errorArg = expressionType;
                    }
                }
                else
                {
                    errorArg = expression.Display;
                }
            }

            if ((object)expressionType == null)
            {
                expressionType = CreateErrorType("error");
            }

            if (errorArg != null)
            {
                hasError = true;
                Error(diagnostics, ErrorCode.ERR_AnonymousTypePropertyAssignedBadValue, errorSyntax, errorArg);
                // NOTE: ERR_QueryRangeVariableAssignedBadValue is being generated 
                //       by query binding code and never reach this point
            }

            return expressionType;
        }
    }
}
