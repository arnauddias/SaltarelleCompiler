﻿using NUnit.Framework;

namespace Saltarelle.Compiler.Tests.Compiler.MethodCompilationTests.StatementTests {
	[TestFixture]
	public class LockStatementTests : MethodCompilerTestBase {
		[Test]
		public void LockStatementEvaluatesArgumentThatDoesNotRequireExtraStatementsAndActsAsABlockStatement() {
			AssertCorrect(
@"public object SomeProperty { get; set; }
public object Method(object o) { return null; }
public void M() {
	object o = null;
	// BEGIN
	lock (Method(SomeProperty = o)) {
		int x = 0;
	}
	// END
}",
@"	this.set_$SomeProperty($o);
	this.$Method($o);
	{
		var $x = 0;
	}
");
		}

		[Test]
		public void LockStatementEvaluatesArgumentThatDoesRequireExtraStatementsAndActsAsABlockStatement() {
			AssertCorrect(
@"public object P1 { get; set; }
public object P2 { get; set; }
public void M() {
	object o = null;
	// BEGIN
	lock (P1 = P2 = o) {
		int x = 0;
	}
	// END
}",
@"	this.set_$P2($o);
	this.set_$P1($o);
	{
		var $x = 0;
	}
");
		}
	}
}
