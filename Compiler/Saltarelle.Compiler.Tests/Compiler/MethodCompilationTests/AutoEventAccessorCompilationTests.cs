﻿using System;
using NUnit.Framework;
using Saltarelle.Compiler.ScriptSemantics;

namespace Saltarelle.Compiler.Tests.Compiler.MethodCompilationTests {
	[TestFixture]
	public class AutoEventAccessorCompilationTests : CompilerTestBase {
		[Test]
		public void InstanceAutoEventAccessorsImplementedAsInstanceMethodsAreCorrectlyCompiled() {
            Compile(new[] { "using System; class C { public event System.EventHandler MyEvent; }" });

			var adder   = FindInstanceMethod("C.add_MyEvent");
			var remover = FindInstanceMethod("C.remove_MyEvent");

			AssertCorrect(adder.Definition,
@"function($value) {
	this.$MyEvent = {Delegate}.Combine(this.$MyEvent, $value);
}");

			AssertCorrect(remover.Definition,
@"function($value) {
	this.$MyEvent = {Delegate}.Remove(this.$MyEvent, $value);
}");

			AssertCorrect(FindClass("C").UnnamedConstructor,
@"function() {
	this.$MyEvent = null;
}");
		}

		[Test]
		public void InstanceAutoEventAccessorsImplementedAsStaticMethodsAreCorrectlyCompiled() {
            Compile(new[] { "using System; class C { public event System.EventHandler MyEvent; }" }, namingConvention: new MockNamingConventionResolver { GetEventSemantics = e => EventScriptSemantics.AddAndRemoveMethods(MethodScriptSemantics.StaticMethodWithThisAsFirstArgument("add_" + e.Name), MethodScriptSemantics.StaticMethodWithThisAsFirstArgument("remove_" + e.Name)) });

			var adder   = FindStaticMethod("C.add_MyEvent");
			var remover = FindStaticMethod("C.remove_MyEvent");

			AssertCorrect(adder.Definition,
@"function($this, $value) {
	$this.$MyEvent = {Delegate}.Combine($this.$MyEvent, $value);
}");

			AssertCorrect(remover.Definition,
@"function($this, $value) {
	$this.$MyEvent = {Delegate}.Remove($this.$MyEvent, $value);
}");

			AssertCorrect(FindClass("C").UnnamedConstructor,
@"function() {
	this.$MyEvent = null;
}");
		}

		[Test]
		public void StaticAutoEventAccessorsAreCorrectlyCompiled() {
            Compile(new[] { "using System; class C { public static event System.EventHandler MyEvent; }" });

			var adder   = FindStaticMethod("C.add_MyEvent");
			var remover = FindStaticMethod("C.remove_MyEvent");

			AssertCorrect(adder.Definition,
@"function($value) {
	{C}.$MyEvent = {Delegate}.Combine({C}.$MyEvent, $value);
}");

			AssertCorrect(remover.Definition,
@"function($value) {
	{C}.$MyEvent = {Delegate}.Remove({C}.$MyEvent, $value);
}");

			var c = FindClass("C");
			Assert.That(c.StaticInitStatements, Has.Count.EqualTo(1));
			Assert.That(OutputFormatter.Format(c.StaticInitStatements[0], allowIntermediates: true), Is.EqualTo("{C}.$MyEvent = null;" + Environment.NewLine));
		}

		[Test]
		public void StaticAutoEventAccessorsAreCorrectlyCompiledForGenericClasses() {
            Compile(new[] { "using System; class C<T> { public static event System.EventHandler MyEvent; }" });

			var adder   = FindStaticMethod("C.add_MyEvent");
			var remover = FindStaticMethod("C.remove_MyEvent");

			AssertCorrect(adder.Definition,
@"function($value) {
	$InstantiateGenericType({C}, $T).$MyEvent = {Delegate}.Combine($InstantiateGenericType({C}, $T).$MyEvent, $value);
}");

			AssertCorrect(remover.Definition,
@"function($value) {
	$InstantiateGenericType({C}, $T).$MyEvent = {Delegate}.Remove($InstantiateGenericType({C}, $T).$MyEvent, $value);
}");

			var c = FindClass("C");
			Assert.That(c.StaticInitStatements, Has.Count.EqualTo(1));
			Assert.That(OutputFormatter.Format(c.StaticInitStatements[0], allowIntermediates: true), Is.EqualTo("$InstantiateGenericType({C}, $T).$MyEvent = null;" + Environment.NewLine));
		}
	}
}
