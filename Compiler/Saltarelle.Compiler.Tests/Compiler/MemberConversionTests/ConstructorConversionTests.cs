﻿using System;
using System.Linq;
using FluentAssertions;
using ICSharpCode.NRefactory.TypeSystem;
using NUnit.Framework;
using Saltarelle.Compiler.ScriptSemantics;

namespace Saltarelle.Compiler.Tests.Compiler.MemberConversionTests {
    [TestFixture]
    public class ConstructorConversionTests : CompilerTestBase {
        [Test]
        public void DefaultConstructorIsInsertedIfNoConstructorIsDefined() {
            Compile(new[] { "class C {}" });
            var cls = FindClass("C");
            cls.NamedConstructors.Should().BeEmpty();
            cls.UnnamedConstructor.Should().NotBeNull();
            cls.UnnamedConstructor.ParameterNames.Should().HaveCount(0);
        }

        [Test]
        public void DefaultConstructorImplementedAsStaticMethodWorks() {
            var namingConvention = new MockNamingConventionResolver { GetConstructorSemantics = ctor => ConstructorScriptSemantics.StaticMethod("X") };
            Compile(new[] { "class C { }" }, namingConvention: namingConvention);
            FindStaticMethod("C.X").Should().NotBeNull();
            FindNamedConstructor("C.X").Should().BeNull();
        }

        [Test]
        public void DefaultConstructorIsNotInsertedIfOtherConstructorIsDefined() {
			var namingConvention = new MockNamingConventionResolver() { GetConstructorSemantics = c => c.Parameters.Count == 0 ? ConstructorScriptSemantics.Unnamed() : ConstructorScriptSemantics.Named("ctor$" + string.Join("$", c.Parameters.Select(p => p.Type.Name))) };
            Compile(new[] { "class C { C(int i) {} }" }, namingConvention: namingConvention);
            var cls = FindClass("C");
            cls.UnnamedConstructor.Should().BeNull();
            cls.NamedConstructors.Should().HaveCount(1);
            cls.NamedConstructors[0].Name.Should().Be("ctor$Int32");
            cls.NamedConstructors[0].Definition.Should().NotBeNull();
        }

        [Test]
        public void ConstructorsCanBeOverloadedWithDifferentImplementations() {
            var namingConvention = new MockNamingConventionResolver { GetConstructorSemantics = ctor => ctor.Parameters[0].Type.Name == "String" ? ConstructorScriptSemantics.Named("StringCtor") : ConstructorScriptSemantics.StaticMethod("IntCtor") };
            Compile(new[] { "class C { C(int i) {} C(string s) {} }" }, namingConvention: namingConvention);
            FindClass("C").NamedConstructors.Should().HaveCount(1);
            FindClass("C").StaticMethods.Should().HaveCount(1);
            FindNamedConstructor("C.StringCtor").Should().NotBeNull();
            FindStaticMethod("C.IntCtor").Should().NotBeNull();
        }

        [Test]
        public void ConstructorImplementedAsStaticMethodGetsAddedToTheStaticMethodsCollectionAndNotTheConstructors() {
            var namingConvention = new MockNamingConventionResolver { GetConstructorSemantics = ctor => ConstructorScriptSemantics.StaticMethod("X") };
            Compile(new[] { "class C { public C() {} }" }, namingConvention: namingConvention);
            FindStaticMethod("C.X").Should().NotBeNull();
            FindNamedConstructor("C.X").Should().BeNull();
        }

        [Test]
        public void ConstructorImplementedAsNotUsableFromScriptDoesNotAppearOnTheType() {
            var namingConvention = new MockNamingConventionResolver { GetConstructorSemantics = ctor => ConstructorScriptSemantics.NotUsableFromScript() };
            Compile(new[] { "class C { public C() {} }" }, namingConvention: namingConvention);
            FindClass("C").UnnamedConstructor.Should().BeNull();
        }

        [Test]
        public void ConstructorImplementedAsInlineCodeDoesNotAppearOnTheType() {
            var namingConvention = new MockNamingConventionResolver { GetConstructorSemantics = ctor => ConstructorScriptSemantics.InlineCode("X") };
            Compile(new[] { "class C { public C() {} }" }, namingConvention: namingConvention);
            FindClass("C").UnnamedConstructor.Should().BeNull();
        }

        [Test]
        public void ConstructorImplementedAsJsonDoesNotAppearOnTheType() {
            var namingConvention = new MockNamingConventionResolver { GetConstructorSemantics = ctor => ConstructorScriptSemantics.Json(new IMember[0]) };
            Compile(new[] { "class C { public C() {} }" }, namingConvention: namingConvention);
            FindClass("C").UnnamedConstructor.Should().BeNull();
        }

        [Test]
        public void StaticConstructorBodyGetsAddedLastInTheStaticInitStatements() {
            var namingConvention = new MockNamingConventionResolver { GetConstructorSemantics = ctor => { if (ctor.IsStatic) throw new InvalidOperationException(); else return ConstructorScriptSemantics.Unnamed(); } };
            Compile(new[] {
@"class C {
    static int x = 0;
    static C() {
        int z = 2;
    }
    static int y = 1;
}" }, namingConvention: namingConvention);

            var cctor = FindClass("C").StaticInitStatements.Aggregate("", (s, st) => s + OutputFormatter.Format(st, true));
            cctor.Replace("\r\n", "\n").Should().Be(
@"{C}.$x = 0;
{C}.$y = 1;
var $z = 2;
".Replace("\r\n", "\n"));
        }

        [Test]
        public void StaticFieldsWithoutInitializersAreInitializedToDefault() {
            var namingConvention = new MockNamingConventionResolver { GetConstructorSemantics = ctor => { if (ctor.IsStatic) throw new InvalidOperationException(); else return ConstructorScriptSemantics.Unnamed(); } };
            Compile(new[] {
@"class C<T> {
    static T x;
    static int y;
	static string z;
}" }, namingConvention: namingConvention);

            var cctor = FindClass("C").StaticInitStatements.Aggregate("", (s, st) => s + OutputFormatter.Format(st, true));
        	cctor.Replace("\r\n", "\n").Should().Be(
@"$InstantiateGenericType({C}, $T).$x = $Default($T);
$InstantiateGenericType({C}, $T).$y = 0;
$InstantiateGenericType({C}, $T).$z = null;
".Replace("\r\n", "\n"));
        }
    }
}
