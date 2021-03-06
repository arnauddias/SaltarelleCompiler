﻿using System;
using System.Collections.Generic;
using System.Linq;
using ICSharpCode.NRefactory;
using ICSharpCode.NRefactory.CSharp;
using ICSharpCode.NRefactory.CSharp.Resolver;
using ICSharpCode.NRefactory.CSharp.TypeSystem;
using ICSharpCode.NRefactory.Semantics;
using ICSharpCode.NRefactory.TypeSystem;
using Saltarelle.Compiler.JSModel;
using Saltarelle.Compiler.JSModel.ExtensionMethods;
using Saltarelle.Compiler.JSModel.Statements;
using Saltarelle.Compiler.JSModel.TypeSystem;
using Saltarelle.Compiler.JSModel.Expressions;
using Saltarelle.Compiler.ScriptSemantics;

namespace Saltarelle.Compiler.Compiler {
	public class Compiler : DepthFirstAstVisitor, ICompiler {
        private class ResolveAllNavigator : IResolveVisitorNavigator {
            public ResolveVisitorNavigationMode Scan(AstNode node) {
                return ResolveVisitorNavigationMode.Resolve;
            }

            public void Resolved(AstNode node, ResolveResult result) {
            }

            public void ProcessConversion(Expression expression, ResolveResult result, Conversion conversion, IType targetType) {
            }
        }

        private readonly INamingConventionResolver _namingConvention;
		private readonly IRuntimeLibrary _runtimeLibrary;
        private readonly IErrorReporter _errorReporter;
        private ICompilation _compilation;
        private CSharpAstResolver _resolver;
        private Dictionary<ITypeDefinition, JsClass> _types;
        private HashSet<Tuple<ConstructorDeclaration, CSharpAstResolver>> _constructorDeclarations;
        private Dictionary<JsClass, List<JsStatement>> _instanceInitStatements;
		private TextLocation _location;
		private ISet<string> _definedSymbols;

        public event Action<IMethod, JsFunctionDefinitionExpression, MethodCompiler> MethodCompiled;

        private void OnMethodCompiled(IMethod method, JsFunctionDefinitionExpression result, MethodCompiler mc) {
            if (MethodCompiled != null)
                MethodCompiled(method, result, mc);
        }

        public Compiler(INamingConventionResolver namingConvention, IRuntimeLibrary runtimeLibrary, IErrorReporter errorReporter) {
            _namingConvention = namingConvention;
            _errorReporter    = errorReporter;
        	_runtimeLibrary   = runtimeLibrary;
        }

		internal bool AllowUnsupportedConstructs { get; set; }

        private JsClass.ClassTypeEnum ConvertClassType(TypeKind typeKind) {
            switch (typeKind) {
                case TypeKind.Class:     return JsClass.ClassTypeEnum.Class;
                case TypeKind.Interface: return JsClass.ClassTypeEnum.Interface;
                case TypeKind.Struct:    return JsClass.ClassTypeEnum.Struct;
                default: throw new ArgumentException("classType");
            }
        }

        private JsClass GetJsClass(ITypeDefinition typeDefinition) {
            JsClass result;
            if (!_types.TryGetValue(typeDefinition, out result)) {
                var semantics = _namingConvention.GetTypeSemantics(typeDefinition);
                if (semantics.GenerateCode) {
					var unusableTypes = Utils.FindUsedUnusableTypes(typeDefinition.GetAllBaseTypes(), _namingConvention).ToList();
					if (unusableTypes.Count > 0) {
						foreach (var ut in unusableTypes)
							_errorReporter.Message(7500, typeDefinition.Region, ut.FullName, typeDefinition.FullName);

						result = new JsClass(typeDefinition, "X", ConvertClassType(typeDefinition.Kind), new string[0], null, null);
					}
					else {
						var baseTypes    = typeDefinition.GetAllBaseTypes().Where(t => _runtimeLibrary.GetScriptType(t, TypeContext.GenericArgument) != null).ToList();

						var baseClass    = typeDefinition.Kind != TypeKind.Interface ? _runtimeLibrary.GetScriptType(baseTypes.Last(t => !t.GetDefinition().Equals(typeDefinition) && t.Kind == TypeKind.Class), TypeContext.Inheritance) : null;    // NRefactory bug/feature: Interfaces are reported as having System.Object as their base type.
						var interfaces   = baseTypes.Where(t => !t.GetDefinition().Equals(typeDefinition) && t.Kind == TypeKind.Interface).Select(t => _runtimeLibrary.GetScriptType(t, TypeContext.Inheritance)).Where(t => t != null).ToList();
						var typeArgNames = semantics.IgnoreGenericArguments ? null : typeDefinition.TypeParameters.Select(a => _namingConvention.GetTypeParameterName(a)).ToList();
						result = new JsClass(typeDefinition, semantics.Name, ConvertClassType(typeDefinition.Kind), typeArgNames, baseClass, interfaces);
					}
                }
                else {
                    result = null;
                }
                _types[typeDefinition] = result;
            }
            return result;
        }

        private void AddInstanceInitStatements(JsClass jsClass, IEnumerable<JsStatement> statements) {
            List<JsStatement> l;
            if (!_instanceInitStatements.TryGetValue(jsClass, out l))
                _instanceInitStatements[jsClass] = l = new List<JsStatement>();
            l.AddRange(statements);
        }

        private List<JsStatement> TryGetInstanceInitStatements(JsClass jsClass) {
            List<JsStatement> l;
            if (_instanceInitStatements.TryGetValue(jsClass, out l))
                return l;
            else
                return new List<JsStatement>();
        }

        private JsEnum ConvertEnum(ITypeDefinition type) {
            var semantics = _namingConvention.GetTypeSemantics(type);
			if (!semantics.GenerateCode)
				return null;

            var values = new List<JsEnumValue>();
            foreach (var f in type.Fields) {
                if (f.ConstantValue != null) {
					var sem = _namingConvention.GetFieldSemantics(f);
					if (sem.Type == FieldScriptSemantics.ImplType.Field) {
						values.Add(new JsEnumValue(sem.Name, Convert.ToInt64(f.ConstantValue)));
					}
                }
                else {
                    _errorReporter.InternalError("Enum field " + type.FullName + "." + f.Name + " is not a DefaultResolvedField", f.Region);
                }
            }

            return new JsEnum(type, semantics.Name, values);
        }

        private IEnumerable<IType> SelfAndNested(IType type) {
            yield return type;
            foreach (var x in type.GetNestedTypes(options: GetMemberOptions.IgnoreInheritedMembers).SelectMany(c => SelfAndNested(c))) {
                yield return x;
            }
        }

		private CSharpParser CreateParser(IEnumerable<string> defineConstants) {
			var parser = new CSharpParser();
			if (defineConstants != null) {
				foreach (var c in defineConstants)
					parser.CompilerSettings.ConditionalSymbols.Add(c);
			}
			return parser;
		}

		public PreparedCompilation CreateCompilation(IEnumerable<ISourceFile> sourceFiles, IEnumerable<IAssemblyReference> references, IList<string> defineConstants) {
            IProjectContent project = new CSharpProjectContent();

            var files = sourceFiles.Select(f => { 
                                                    using (var rdr = f.Open()) {
                                                        var cu = CreateParser(defineConstants).Parse(rdr, f.FileName);
                                                        var expandResult = new QueryExpressionExpander().ExpandQueryExpressions(cu);
                                                        cu = (expandResult != null ? (CompilationUnit)expandResult.AstNode : cu);
                                                        var definedSymbols = DefinedSymbolsGatherer.Gather(cu, defineConstants);
                                                        return new PreparedCompilation.ParsedSourceFile(cu, new CSharpParsedFile(f.FileName, new UsingScope()), definedSymbols);
                                                    }
                                                }).ToList();

            foreach (var f in files) {
                var tcv = new TypeSystemConvertVisitor(f.ParsedFile);
                f.CompilationUnit.AcceptVisitor(tcv);
                project = project.UpdateProjectContent(null, f.ParsedFile);
            }
            project = project.AddAssemblyReferences(references);

            return new PreparedCompilation(project.CreateCompilation(), files);
		}

        public IEnumerable<JsType> Compile(PreparedCompilation compilation) {
			_compilation = compilation.Compilation;

			_namingConvention.Prepare(_compilation.GetAllTypeDefinitions(), _compilation.MainAssembly, _errorReporter);

            _types = new Dictionary<ITypeDefinition, JsClass>();
            _constructorDeclarations = new HashSet<Tuple<ConstructorDeclaration, CSharpAstResolver>>();
            _instanceInitStatements = new Dictionary<JsClass, List<JsStatement>>();

			var unsupportedConstructsScanner = new UnsupportedConstructsScanner(_errorReporter, compilation.Compilation.ReferencedAssemblies.Count == 0);
			bool hasUnsupported = false;

            foreach (var f in compilation.SourceFiles) {
				try {
					if (!AllowUnsupportedConstructs) {
						if (!unsupportedConstructsScanner.ProcessAndReturnTrueIfEverythingIsSupported(f.CompilationUnit)) {
							hasUnsupported = true;
							continue;
						}
					}
					_definedSymbols = f.DefinedSymbols;

	                _resolver = new CSharpAstResolver(_compilation, f.CompilationUnit, f.ParsedFile);
		            _resolver.ApplyNavigator(new ResolveAllNavigator());
			        f.CompilationUnit.AcceptVisitor(this);
				}
				catch (Exception ex) {
					_errorReporter.InternalError(ex, f.ParsedFile.FileName, _location);
				}
            }

			if (hasUnsupported)
				return new JsType[0];	// Just to be safe

            // Handle constructors. We must do this after we have visited all the compilation units because field initializer (which change the InstanceInitStatements and StaticInitStatements) might appear anywhere.
            foreach (var n in _constructorDeclarations) {
				try {
					_resolver = n.Item2;
					HandleConstructorDeclaration(n.Item1);
				}
				catch (Exception ex) {
					_errorReporter.InternalError(ex, n.Item1.GetRegion());
				}
			}

            // Add default constructors where needed.
            foreach (var toAdd in _types.Where(t => t.Value != null).SelectMany(kvp => kvp.Key.GetConstructors().Where(c => c.IsSynthetic).Select(c => new { jsClass = kvp.Value, c }))) {
				try {
					MaybeAddDefaultConstructorToType(toAdd.jsClass, toAdd.c);
				}
				catch (Exception ex) {
					_errorReporter.InternalError(ex, toAdd.c.Region, "Error adding default constructor to type");
				}
			}

            _types.Values.Where(t => t != null).ForEach(t => t.Freeze());

			var enums = new List<JsType>();
			foreach (var e in _compilation.MainAssembly.TopLevelTypeDefinitions.SelectMany(SelfAndNested).Where(t => t.Kind == TypeKind.Enum)) {
				try {
					enums.Add(ConvertEnum(e.GetDefinition()));
				}
				catch (Exception ex) {
					_errorReporter.InternalError(ex, e.GetDefinition().Region);
				}
			}

            return _types.Values.Concat(enums).Where(t => t != null);
        }

        private MethodCompiler CreateMethodCompiler() {
            return new MethodCompiler(_namingConvention, _errorReporter, _compilation, _resolver, _runtimeLibrary, _definedSymbols);
        }

        private void AddCompiledMethodToType(JsClass jsClass, IMethod method, MethodScriptSemantics options, JsMethod jsMethod) {
            if ((options.Type == MethodScriptSemantics.ImplType.NormalMethod && method.IsStatic) || options.Type == MethodScriptSemantics.ImplType.StaticMethodWithThisAsFirstArgument) {
                jsClass.StaticMethods.Add(jsMethod);
            }
            else {
                jsClass.InstanceMethods.Add(jsMethod);
            }
        }

        private void MaybeCompileAndAddMethodToType(JsClass jsClass, EntityDeclaration node, BlockStatement body, IMethod method, MethodScriptSemantics options) {
            if (options.GenerateCode) {
                var typeParamNames = options.IgnoreGenericArguments ? (IEnumerable<string>)new string[0] : method.TypeParameters.Select(tp => _namingConvention.GetTypeParameterName(tp)).ToList();
				JsMethod jsMethod;
				if (method.IsAbstract) {
					jsMethod = new JsMethod(method, options.Name, typeParamNames, null);
				}
				else {
	                var compiled = CompileMethod(node, body, method, options);
		            jsMethod = new JsMethod(method, options.Name, typeParamNames, compiled);
				}
                AddCompiledMethodToType(jsClass, method, options, jsMethod);
            }
        }

        private void AddCompiledConstructorToType(JsClass jsClass, IMethod constructor, ConstructorScriptSemantics options, JsFunctionDefinitionExpression jsConstructor) {
            switch (options.Type) {
                case ConstructorScriptSemantics.ImplType.UnnamedConstructor:
                    if (jsClass.UnnamedConstructor != null) {
                        _errorReporter.Message(7501, constructor.Region, constructor.DeclaringType.FullName);
                    }
                    else {
                        jsClass.UnnamedConstructor = jsConstructor;
                    }
                    break;
                case ConstructorScriptSemantics.ImplType.NamedConstructor:
                    jsClass.NamedConstructors.Add(new JsNamedConstructor(options.Name, jsConstructor));
                    break;

                case ConstructorScriptSemantics.ImplType.StaticMethod:
                    jsClass.StaticMethods.Add(new JsMethod(constructor, options.Name, new string[0], jsConstructor));
                    break;
            }
        }

        private void MaybeCompileAndAddConstructorToType(JsClass jsClass, ConstructorDeclaration node, IMethod constructor, ConstructorScriptSemantics options) {
            if (options.GenerateCode) {
                var mc = CreateMethodCompiler();
                var compiled = mc.CompileConstructor(node, constructor, TryGetInstanceInitStatements(jsClass), options);
                OnMethodCompiled(constructor, compiled, mc);
                AddCompiledConstructorToType(jsClass, constructor, options, compiled);
            }
        }

        private void MaybeAddDefaultConstructorToType(JsClass jsClass, IMethod constructor) {
            var options = _namingConvention.GetConstructorSemantics(constructor);
            if (options.GenerateCode) {
                var mc = CreateMethodCompiler();
                var compiled = mc.CompileDefaultConstructor(constructor, TryGetInstanceInitStatements(jsClass), options);
                OnMethodCompiled(constructor, compiled, mc);
                AddCompiledConstructorToType(jsClass, constructor, options, compiled);
            }
        }

        private JsFunctionDefinitionExpression CompileMethod(EntityDeclaration node, BlockStatement body, IMethod method, MethodScriptSemantics options) {
            var mc = CreateMethodCompiler();
            var result = mc.CompileMethod(node, body, method, options);
            OnMethodCompiled(method, result, mc);
            return result;
        }

        private void CompileAndAddAutoPropertyMethodsToType(JsClass jsClass, IProperty property, PropertyScriptSemantics options, string backingFieldName) {
            if (options.GetMethod != null && options.GetMethod.GenerateCode) {
                var compiled = CreateMethodCompiler().CompileAutoPropertyGetter(property, options, backingFieldName);
                AddCompiledMethodToType(jsClass, property.Getter, options.GetMethod, new JsMethod(property.Getter, options.GetMethod.Name, new string[0], compiled));
            }
            if (options.SetMethod != null && options.SetMethod.GenerateCode) {
                var compiled = CreateMethodCompiler().CompileAutoPropertySetter(property, options, backingFieldName);
                AddCompiledMethodToType(jsClass, property.Setter, options.SetMethod, new JsMethod(property.Setter, options.SetMethod.Name, new string[0], compiled));
            }
        }

        private void CompileAndAddAutoEventMethodsToType(JsClass jsClass, EventDeclaration node, IEvent evt, EventScriptSemantics options, string backingFieldName) {
            if (options.AddMethod != null && options.AddMethod.GenerateCode) {
                var compiled = CreateMethodCompiler().CompileAutoEventAdder(evt, options, backingFieldName);
                AddCompiledMethodToType(jsClass, evt.AddAccessor, options.AddMethod, new JsMethod(evt.AddAccessor, options.AddMethod.Name, new string[0], compiled));
            }
            if (options.RemoveMethod != null && options.RemoveMethod.GenerateCode) {
                var compiled = CreateMethodCompiler().CompileAutoEventRemover(evt, options, backingFieldName);
                AddCompiledMethodToType(jsClass, evt.RemoveAccessor, options.RemoveMethod, new JsMethod(evt.RemoveAccessor, options.RemoveMethod.Name, new string[0], compiled));
            }
        }

        private void AddDefaultFieldInitializerToType(JsClass jsClass, string fieldName, IMember member, IType fieldType, ITypeDefinition owningType, bool isStatic) {
            if (isStatic) {
                jsClass.StaticInitStatements.AddRange(CreateMethodCompiler().CompileDefaultFieldInitializer(member.Region.FileName, member.Region.Begin, JsExpression.MemberAccess(_runtimeLibrary.GetScriptType(owningType, TypeContext.Instantiation), fieldName), fieldType));
            }
            else {
                AddInstanceInitStatements(jsClass, CreateMethodCompiler().CompileDefaultFieldInitializer(member.Region.FileName, member.Region.Begin, JsExpression.MemberAccess(JsExpression.This, fieldName), fieldType));
            }
        }

        private void CompileAndAddFieldInitializerToType(JsClass jsClass, string fieldName, ITypeDefinition owningType, Expression initializer, bool isStatic) {
            if (isStatic) {
                jsClass.StaticInitStatements.AddRange(CreateMethodCompiler().CompileFieldInitializer(initializer.GetRegion().FileName, initializer.StartLocation, JsExpression.MemberAccess(_runtimeLibrary.GetScriptType(owningType, TypeContext.Instantiation), fieldName), initializer));
            }
            else {
                AddInstanceInitStatements(jsClass, CreateMethodCompiler().CompileFieldInitializer(initializer.GetRegion().FileName, initializer.StartLocation, JsExpression.MemberAccess(JsExpression.This, fieldName), initializer));
            }
        }

		protected override void VisitChildren(AstNode node) {
			AstNode next;
			for (var child = node.FirstChild; child != null; child = next) {
				// Store next to allow the loop to continue
				// if the visitor removes/replaces child.
				next = child.NextSibling;
				_location = child.StartLocation;
				child.AcceptVisitor (this);
			}
		}

        public override void VisitTypeDeclaration(TypeDeclaration typeDeclaration) {
            if (typeDeclaration.ClassType == ClassType.Class || typeDeclaration.ClassType == ClassType.Interface || typeDeclaration.ClassType == ClassType.Struct) {
                var resolveResult = _resolver.Resolve(typeDeclaration);
                if (!(resolveResult is TypeResolveResult)) {
                    _errorReporter.InternalError("Type declaration " + typeDeclaration.Name + " does not resolve to a type.", typeDeclaration.GetRegion());
                    return;
                }
                GetJsClass(resolveResult.Type.GetDefinition());

                base.VisitTypeDeclaration(typeDeclaration);
            }
        }

        public override void VisitMethodDeclaration(MethodDeclaration methodDeclaration) {
            var resolveResult = _resolver.Resolve(methodDeclaration);
            if (!(resolveResult is MemberResolveResult)) {
                _errorReporter.InternalError("Method declaration " + methodDeclaration.Name + " does not resolve to a member.", methodDeclaration.GetRegion());
                return;
            }
            var method = ((MemberResolveResult)resolveResult).Member as IMethod;
            if (method == null) {
                _errorReporter.InternalError("Method declaration " + methodDeclaration.Name + " does not resolve to a method (resolves to " + resolveResult.ToString() + ")", methodDeclaration.GetRegion());
                return;
            }

            var jsClass = GetJsClass(method.DeclaringTypeDefinition);
            if (jsClass == null)
                return;

            if (method.IsAbstract || !methodDeclaration.Body.IsNull) {	// The second condition is used to ignore partial method parts without definitions.
                MaybeCompileAndAddMethodToType(jsClass, methodDeclaration, methodDeclaration.Body, method, _namingConvention.GetMethodSemantics(method));
            }
        }

        public override void VisitOperatorDeclaration(OperatorDeclaration operatorDeclaration) {
            var resolveResult = _resolver.Resolve(operatorDeclaration);
            if (!(resolveResult is MemberResolveResult)) {
                _errorReporter.InternalError("Operator declaration " + OperatorDeclaration.GetName(operatorDeclaration.OperatorType) + " does not resolve to a member.", operatorDeclaration.GetRegion());
                return;
            }
            var method = ((MemberResolveResult)resolveResult).Member as IMethod;
            if (method == null) {
                _errorReporter.InternalError("Operator declaration " + OperatorDeclaration.GetName(operatorDeclaration.OperatorType) + " does not resolve to a method (resolves to " + resolveResult.ToString() + ")", operatorDeclaration.GetRegion());
                return;
            }

            var jsClass = GetJsClass(method.DeclaringTypeDefinition);
            if (jsClass == null)
                return;

            MaybeCompileAndAddMethodToType(jsClass, operatorDeclaration, operatorDeclaration.Body, method, _namingConvention.GetMethodSemantics(method));
        }

        private void HandleConstructorDeclaration(ConstructorDeclaration constructorDeclaration) {
            var resolveResult = _resolver.Resolve(constructorDeclaration);
            if (!(resolveResult is MemberResolveResult)) {
                _errorReporter.InternalError("Method declaration " + constructorDeclaration.Name + " does not resolve to a member.", constructorDeclaration.GetRegion());
                return;
            }
            var method = ((MemberResolveResult)resolveResult).Member as IMethod;
            if (method == null) {
                _errorReporter.InternalError("Method declaration " + constructorDeclaration.Name + " does not resolve to a method (resolves to " + resolveResult.ToString() + ")", constructorDeclaration.GetRegion());
                return;
            }

            var jsClass = GetJsClass(method.DeclaringTypeDefinition);
            if (jsClass == null)
                return;

            if (method.IsStatic) {
                jsClass.StaticInitStatements.AddRange(CompileMethod(constructorDeclaration, constructorDeclaration.Body, method, MethodScriptSemantics.NormalMethod("X")).Body.Statements);
            }
            else {
                MaybeCompileAndAddConstructorToType(jsClass, constructorDeclaration, method, _namingConvention.GetConstructorSemantics(method));
            }
        }


        public override void VisitConstructorDeclaration(ConstructorDeclaration constructorDeclaration) {
            _constructorDeclarations.Add(Tuple.Create(constructorDeclaration, _resolver));
        }

        public override void VisitPropertyDeclaration(PropertyDeclaration propertyDeclaration) {
            var resolveResult = _resolver.Resolve(propertyDeclaration);
            if (!(resolveResult is MemberResolveResult)) {
                _errorReporter.InternalError("Property declaration " + propertyDeclaration.Name + " does not resolve to a member.", propertyDeclaration.GetRegion());
                return;
            }

            var property = ((MemberResolveResult)resolveResult).Member as IProperty;
            if (property == null) {
                _errorReporter.InternalError("Property declaration " + propertyDeclaration.Name + " does not resolve to a property (resolves to " + resolveResult.ToString() + ")", propertyDeclaration.GetRegion());
                return;
            }

            var jsClass = GetJsClass(property.DeclaringTypeDefinition);
            if (jsClass == null)
                return;

            var impl = _namingConvention.GetPropertySemantics(property);

            switch (impl.Type) {
                case PropertyScriptSemantics.ImplType.GetAndSetMethods: {
                    if (!property.IsAbstract && propertyDeclaration.Getter.Body.IsNull && propertyDeclaration.Setter.Body.IsNull) {
                        // Auto-property
                        if ((impl.GetMethod != null && impl.GetMethod.GenerateCode) || (impl.SetMethod != null && impl.SetMethod.GenerateCode)) {
                            var fieldName = _namingConvention.GetAutoPropertyBackingFieldName(property);
                            AddDefaultFieldInitializerToType(jsClass, fieldName, property, property.ReturnType, property.DeclaringTypeDefinition, property.IsStatic);
                            CompileAndAddAutoPropertyMethodsToType(jsClass, property, impl, fieldName);
                        }
                    }
                    else {
                        if (!propertyDeclaration.Getter.IsNull) {
                            MaybeCompileAndAddMethodToType(jsClass, propertyDeclaration.Getter, propertyDeclaration.Getter.Body, property.Getter, impl.GetMethod);
                        }

                        if (!propertyDeclaration.Setter.IsNull) {
                            MaybeCompileAndAddMethodToType(jsClass, propertyDeclaration.Setter, propertyDeclaration.Setter.Body, property.Setter, impl.SetMethod);
                        }
                    }
                    break;
                }
                case PropertyScriptSemantics.ImplType.Field: {
                    AddDefaultFieldInitializerToType(jsClass, impl.FieldName, property, property.ReturnType, property.DeclaringTypeDefinition, property.IsStatic);
                    break;
                }
                case PropertyScriptSemantics.ImplType.NotUsableFromScript: {
                    break;
                }
                default: {
                    throw new InvalidOperationException("Invalid property implementation " + impl.Type);
                }
            }
        }

        public override void VisitEventDeclaration(EventDeclaration eventDeclaration) {
            foreach (var singleEvt in eventDeclaration.Variables) {
                var resolveResult = _resolver.Resolve(singleEvt);
                if (!(resolveResult is MemberResolveResult)) {
                    _errorReporter.InternalError("Event declaration " + singleEvt.Name + " does not resolve to a member.", eventDeclaration.GetRegion());
                    return;
                }

                var evt = ((MemberResolveResult)resolveResult).Member as IEvent;
                if (evt == null) {
                    _errorReporter.InternalError("Event declaration " + singleEvt.Name + " does not resolve to an event (resolves to " + resolveResult.ToString() + ")", eventDeclaration.GetRegion());
                    return;
                }

                var jsClass = GetJsClass(evt.DeclaringTypeDefinition);
                if (jsClass == null)
                    return;

                var impl = _namingConvention.GetEventSemantics(evt);
                switch (impl.Type) {
                    case EventScriptSemantics.ImplType.AddAndRemoveMethods: {
                        if ((impl.AddMethod != null && impl.AddMethod.GenerateCode) || (impl.RemoveMethod != null && impl.RemoveMethod.GenerateCode)) {
							if (evt.IsAbstract) {
								if (impl.AddMethod.GenerateCode)
									AddCompiledMethodToType(jsClass, evt.AddAccessor, impl.AddMethod, new JsMethod(evt.AddAccessor, impl.AddMethod.Name, null, null));
								if (impl.RemoveMethod.GenerateCode)
									AddCompiledMethodToType(jsClass, evt.RemoveAccessor, impl.RemoveMethod, new JsMethod(evt.RemoveAccessor, impl.RemoveMethod.Name, null, null));
							}
							else {
	                            var fieldName = _namingConvention.GetAutoEventBackingFieldName(evt);
		                        if (singleEvt.Initializer.IsNull) {
			                        AddDefaultFieldInitializerToType(jsClass, fieldName, evt, evt.ReturnType, evt.DeclaringTypeDefinition, evt.IsStatic);
				                }
								else {
					                CompileAndAddFieldInitializerToType(jsClass, fieldName, evt.DeclaringTypeDefinition, singleEvt.Initializer, evt.IsStatic);
						        }

	                            CompileAndAddAutoEventMethodsToType(jsClass, eventDeclaration, evt, impl, fieldName);
							}
                        }
                        break;
                    }

                    case EventScriptSemantics.ImplType.NotUsableFromScript: {
                        break;
                    }

                    default: {
                        throw new InvalidOperationException("Invalid event implementation type");
                    }
                }
            }
        }

        public override void VisitCustomEventDeclaration(CustomEventDeclaration eventDeclaration) {
            var resolveResult = _resolver.Resolve(eventDeclaration);
            if (!(resolveResult is MemberResolveResult)) {
                _errorReporter.InternalError("Event declaration " + eventDeclaration.Name + " does not resolve to a member.", eventDeclaration.GetRegion());
                return;
            }

            var evt = ((MemberResolveResult)resolveResult).Member as IEvent;
            if (evt == null) {
                _errorReporter.InternalError("Event declaration " + eventDeclaration.Name + " does not resolve to an event (resolves to " + resolveResult.ToString() + ")", eventDeclaration.GetRegion());
                return;
            }

            var jsClass = GetJsClass(evt.DeclaringTypeDefinition);
            if (jsClass == null)
                return;

            var impl = _namingConvention.GetEventSemantics(evt);

            switch (impl.Type) {
                case EventScriptSemantics.ImplType.AddAndRemoveMethods: {
                    if (!eventDeclaration.AddAccessor.IsNull) {
                        MaybeCompileAndAddMethodToType(jsClass, eventDeclaration.AddAccessor, eventDeclaration.AddAccessor.Body, evt.AddAccessor, impl.AddMethod);
                    }

                    if (!eventDeclaration.RemoveAccessor.IsNull) {
                        MaybeCompileAndAddMethodToType(jsClass, eventDeclaration.RemoveAccessor, eventDeclaration.RemoveAccessor.Body, evt.RemoveAccessor, impl.RemoveMethod);
                    }
                    break;
                }
                case EventScriptSemantics.ImplType.NotUsableFromScript: {
                    break;
                }
                default: {
                    throw new InvalidOperationException("Invalid event implementation type");
                }
            }
        }

        public override void VisitFieldDeclaration(FieldDeclaration fieldDeclaration) {
            foreach (var v in fieldDeclaration.Variables) {
                var resolveResult = _resolver.Resolve(v);
                if (!(resolveResult is MemberResolveResult)) {
                    _errorReporter.InternalError("Field declaration " + v.Name + " does not resolve to a member.", fieldDeclaration.GetRegion());
                    return;
                }

                var field = ((MemberResolveResult)resolveResult).Member as IField;
                if (field == null) {
                    _errorReporter.InternalError("Field declaration " + v.Name + " does not resolve to a field (resolves to " + resolveResult.ToString() + ")", fieldDeclaration.GetRegion());
                    return;
                }

                var jsClass = GetJsClass(field.DeclaringTypeDefinition);
                if (jsClass == null)
                    return;

                var impl = _namingConvention.GetFieldSemantics(field);
				if (impl.GenerateCode) {
                    if (v.Initializer.IsNull) {
                        AddDefaultFieldInitializerToType(jsClass, impl.Name, field, field.ReturnType, field.DeclaringTypeDefinition, field.IsStatic);
                    }
                    else {
                        CompileAndAddFieldInitializerToType(jsClass, impl.Name, field.DeclaringTypeDefinition, v.Initializer, field.IsStatic);
                    }
				}
            }
        }

        public override void VisitIndexerDeclaration(IndexerDeclaration indexerDeclaration) {
            var resolveResult = _resolver.Resolve(indexerDeclaration);
            if (!(resolveResult is MemberResolveResult)) {
                _errorReporter.InternalError("Event declaration " + indexerDeclaration.Name + " does not resolve to a member.", indexerDeclaration.GetRegion());
                return;
            }

            var prop = ((MemberResolveResult)resolveResult).Member as IProperty;
            if (prop == null) {
                _errorReporter.InternalError("Event declaration " + indexerDeclaration.Name + " does not resolve to a property (resolves to " + resolveResult.ToString() + ")", indexerDeclaration.GetRegion());
                return;
            }

            var jsClass = GetJsClass(prop.DeclaringTypeDefinition);
            if (jsClass == null)
                return;

            var impl = _namingConvention.GetPropertySemantics(prop);

            switch (impl.Type) {
                case PropertyScriptSemantics.ImplType.GetAndSetMethods: {
                    if (!indexerDeclaration.Getter.IsNull)
                        MaybeCompileAndAddMethodToType(jsClass, indexerDeclaration.Getter, indexerDeclaration.Getter.Body, prop.Getter, impl.GetMethod);
                    if (!indexerDeclaration.Setter.IsNull)
                        MaybeCompileAndAddMethodToType(jsClass, indexerDeclaration.Setter, indexerDeclaration.Setter.Body, prop.Setter, impl.SetMethod);
                    break;
                }
                case PropertyScriptSemantics.ImplType.NotUsableFromScript:
                    break;
                default:
                    throw new InvalidOperationException("Invalid indexer implementation type " + impl.Type);
            }
        }
    }
}
