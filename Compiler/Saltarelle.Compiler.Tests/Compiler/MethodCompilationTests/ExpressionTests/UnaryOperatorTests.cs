﻿using NUnit.Framework;

namespace Saltarelle.Compiler.Tests.Compiler.MethodCompilationTests.ExpressionTests {
	[TestFixture]
	public class UnaryOperatorTests : MethodCompilerTestBase {
		[Test]
		public void NonLiftedUnaryPlusWorks() {
			AssertCorrect(
@"public void M() {
	int a = 0;
	// BEGIN
	var b = +a;
	// END
}",
@"	var $b = +$a;
");
		}

		[Test]
		public void NonLiftedUnaryMinusWorks() {
			AssertCorrect(
@"public void M() {
	int a = 0;
	// BEGIN
	var b = -a;
	// END
}",
@"	var $b = -$a;
");
		}

		[Test]
		public void NonLiftedLogicalNotWorks() {
			AssertCorrect(
@"public void M() {
	bool a = false;
	// BEGIN
	var b = !a;
	// END
}",
@"	var $b = !$a;
");
		}

		[Test]
		public void NonLiftedBitwiseNotWorks() {
			AssertCorrect(
@"public void M() {
	int a = 0;
	// BEGIN
	var b = ~a;
	// END
}",
@"	var $b = ~$a;
");
		}

		[Test]
		public void LiftedUnaryPlusWorks() {
			AssertCorrect(
@"public void M() {
	int? a = 0;
	// BEGIN
	var b = +a;
	// END
}",
@"	var $b = $Lift(+$a);
");
		}

		[Test]
		public void LiftedUnaryMinusWorks() {
			AssertCorrect(
@"public void M() {
	int? a = 0;
	// BEGIN
	var b = -a;
	// END
}",
@"	var $b = $Lift(-$a);
");
		}

		[Test]
		public void LiftedLogicalNotWorks() {
			AssertCorrect(
@"public void M() {
	bool? a = false;
	// BEGIN
	var b = !a;
	// END
}",
@"	var $b = $Lift(!$a);
");
		}

		[Test]
		public void LiftedBitwiseNotWorks() {
			AssertCorrect(
@"public void M() {
	int? a = 0;
	// BEGIN
	var b = ~a;
	// END
}",
@"	var $b = $Lift(~$a);
");
		}

		[Test]
		public void UnaryOperatorsWorkForDynamicMembers() {
			AssertCorrect(
@"public void M() {
	dynamic d = null;
	// BEGIN
	var i = +d.someMember;
	// END
}",
@"	var $i = +$d.someMember;
");

			AssertCorrect(
@"public void M() {
	dynamic d = null;
	// BEGIN
	var i = -d.someMember;
	// END
}",
@"	var $i = -$d.someMember;
");

			AssertCorrect(
@"public void M() {
	dynamic d = null;
	// BEGIN
	var i = !d.someMember;
	// END
}",
@"	var $i = !$d.someMember;
");

			AssertCorrect(
@"public void M() {
	dynamic d = null;
	// BEGIN
	var i = ~d.someMember;
	// END
}",
@"	var $i = ~$d.someMember;
");
		}

		[Test]
		public void UnaryOperatorsWorkForDynamicObjects() {
			AssertCorrect(
@"public void M() {
	dynamic d = null;
	// BEGIN
	var i = +d;
	// END
}",
@"	var $i = +$d;
");

			AssertCorrect(
@"public void M() {
	dynamic d = null;
	// BEGIN
	var i = -d;
	// END
}",
@"	var $i = -$d;
");

			AssertCorrect(
@"public void M() {
	dynamic d = null;
	// BEGIN
	var i = !d;
	// END
}",
@"	var $i = !$d;
");

			AssertCorrect(
@"public void M() {
	dynamic d = null;
	// BEGIN
	var i = ~d;
	// END
}",
@"	var $i = ~$d;
");
		}
	}
}
