﻿// Copyright (c) 2007-2014 Thong Nguyen (tumtumtum@gmail.com)

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq.Expressions;
﻿using Shaolinq.Persistence;
﻿using Shaolinq.Persistence.Linq;
using Shaolinq.Persistence.Linq.Expressions;

namespace Shaolinq.Sqlite
{
	public class SqliteSqlQueryFormatter
		: Sql92QueryFormatter
	{
		public SqliteSqlQueryFormatter(SqlQueryFormatterOptions options, SqlDialect sqlDialect,  SqlDataTypeProvider sqlDataTypeProvider)
            : base(options, sqlDialect, sqlDataTypeProvider)
		{
		}

		protected override Expression PreProcess(Expression expression)
		{
			IDictionary<string, string> primaryKeyNameByTablesWithReducedPrimaryKeyName;

			expression = base.PreProcess(expression);
			expression = SqliteAutoIncrementPrimaryKeyColumnReducer.Reduce(expression, out primaryKeyNameByTablesWithReducedPrimaryKeyName);
			expression = SqliteForeignKeyConstraintReducer.Reduce(expression, primaryKeyNameByTablesWithReducedPrimaryKeyName);
			
			return expression;
		}

		protected override void WriteDeferrability(SqlColumnReferenceDeferrability deferrability)
		{
			switch (deferrability)
			{
				case SqlColumnReferenceDeferrability.Deferrable:
					this.Write(" DEFERRABLE");
					break;
				case SqlColumnReferenceDeferrability.InitiallyDeferred:
					this.Write(" DEFERRABLE INITIALLY DEFERRED");
					break;
				case SqlColumnReferenceDeferrability.InitiallyImmediate:
					this.Write(" DEFERRABLE INITIALLY IMMEDIATE");
					break;
			}
		}

		protected override FunctionResolveResult ResolveSqlFunction(SqlFunctionCallExpression functionCallExpression)
		{
			var function = functionCallExpression.Function;
			var arguments = functionCallExpression.Arguments;

			switch (function)
			{
				case SqlFunction.Concat:
					return new FunctionResolveResult("||", true, arguments);
				case SqlFunction.ServerDateTime:
					return new FunctionResolveResult("STRFTIME", false, FunctionResolveResult.MakeArguments("%Y-%m-%d %H:%M:%f0000", "now", "localtime"), null, arguments);
				case SqlFunction.Year:
					return new FunctionResolveResult("STRFTIME", false, FunctionResolveResult.MakeArguments("%Y"), null, arguments);
				case SqlFunction.Month:
					return new FunctionResolveResult("STRFTIME", false, FunctionResolveResult.MakeArguments("%m"), null, arguments);
				case SqlFunction.Week:
					return new FunctionResolveResult("STRFTIME", false, FunctionResolveResult.MakeArguments("%W"), null, arguments);
				case SqlFunction.DayOfYear:
					return new FunctionResolveResult("STRFTIME", false, FunctionResolveResult.MakeArguments("%j"), null, arguments);
				case SqlFunction.DayOfMonth:
					return new FunctionResolveResult("STRFTIME", false, FunctionResolveResult.MakeArguments("%d"), null, arguments);
				case SqlFunction.DayOfWeek:
					return new FunctionResolveResult("STRFTIME", false, FunctionResolveResult.MakeArguments("%w"), null, arguments);
				case SqlFunction.Substring:
					return new FunctionResolveResult("SUBSTR", false, arguments);
				case SqlFunction.TrimLeft:
					return new FunctionResolveResult("LTRIM", false, arguments);
				case SqlFunction.TrimRight:
					return new FunctionResolveResult("RTRIM", false, arguments);
			}

			return base.ResolveSqlFunction(functionCallExpression);
		}

		protected override Expression VisitFunctionCall(SqlFunctionCallExpression functionCallExpression)
		{
			switch (functionCallExpression.Function)
			{
				case SqlFunction.Year:
				case SqlFunction.Week:
				case SqlFunction.DayOfYear:
				case SqlFunction.DayOfMonth:
				case SqlFunction.DayOfWeek:
				case SqlFunction.Minute:
				case SqlFunction.Second:
				case SqlFunction.Hour:
					this.Write("(CAST (");
					base.VisitFunctionCall(functionCallExpression);
					this.Write(" AS INTEGER))");

					return functionCallExpression;
			}

			return base.VisitFunctionCall(functionCallExpression);
		}


		protected override void AppendLimit(SqlSelectExpression selectExpression)
		{
			if (selectExpression.Skip != null || selectExpression.Take != null)
			{
				if (selectExpression.Take != null)
				{
					this.Write(" LIMIT ");

					Visit(selectExpression.Take);
				}
				else
				{
					this.WriteLine(" LIMIT -1 ");
				}

				if (selectExpression.Skip != null)
				{
					this.Write(" OFFSET ");

					Visit(selectExpression.Skip);
				}
			}
		}

		protected override void WriteInsertIntoReturning(SqlInsertIntoExpression expression)
		{
			if (expression.ReturningAutoIncrementColumnNames == null
				|| expression.ReturningAutoIncrementColumnNames.Count == 0)
			{
				return;
			}

			this.Write("; SELECT last_insert_rowid()");
		}

		protected override void WriteInsertDefaultValuesSuffix()
		{
			this.Write(" DEFAULT VALUES");
		}
	}
}
