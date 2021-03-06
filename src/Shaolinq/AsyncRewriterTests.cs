﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace Shaolinq.AsyncRewriter.Tests
{
	[TestFixture]
	public class AsyncRewriterTests
	{
		[Test]
		public void TestRewrite()
		{
			var rewriter = new Rewriter();
			var root = Path.GetDirectoryName(new Uri(this.GetType().Assembly.CodeBase).LocalPath);
			var paths = new List<string> { "Foo.cs", "Bar.cs" };
			
			var result = rewriter.RewriteAndMerge(paths.Select(c => Path.Combine(root, c)).ToArray());

			Console.WriteLine(result);
		}

		[Test]
		public void TestWithGenericConstraints()
		{
			var rewriter = new Rewriter();
			var root = Path.GetDirectoryName(new Uri(this.GetType().Assembly.CodeBase).LocalPath);
			var paths = new List<string> { "IQuery.cs" };

			var result = rewriter.RewriteAndMerge(paths.Select(c => Path.Combine(root, c)).ToArray());

			Console.WriteLine(result);
		}
	}
}
