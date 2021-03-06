﻿using NUnit.Framework;

namespace Saltarelle.Compiler.Tests.Compiler.MethodCompilationTests.StatementTests {
	[TestFixture]
	public class ReturnStatementTests : MethodCompilerTestBase {
		[Test]
		public void ReturnVoidStatementWorks() {
			AssertCorrect(
@"public void M() {
	// BEGIN
	return;
	// END
}",
@"	return;
");
		}

		[Test]
		public void ReturnExpressionStatementWithExpressionWithoutExtraStatementsWorks() {
			AssertCorrect(
@"public int M() {
	int x = 0;
	// BEGIN
	return x;
	// END
}",
@"	return $x;
");
		}

		[Test]
		public void ReturnExpressionStatementWithExpressionWithExtraStatementsWorks() {
			AssertCorrect(
@"public int SomeProperty { get; set; }
public void M() {
	// BEGIN
	return (SomeProperty = 1);
	// END
}",
@"	this.set_$SomeProperty(1);
	return 1;
");
		}
	}
}
