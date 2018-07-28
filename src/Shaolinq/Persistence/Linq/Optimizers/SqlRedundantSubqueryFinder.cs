// Copyright (c) 2007-2018 Thong Nguyen (tumtumtum@gmail.com)

using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Shaolinq.Persistence.Linq.Expressions;

namespace Shaolinq.Persistence.Linq.Optimizers
{
	public class SqlRedundantSubqueryFinder
		: SqlExpressionVisitor
	{
		List<SqlSelectExpression> redundant;

		private SqlRedundantSubqueryFinder()
		{
		}

		internal static List<SqlSelectExpression> Find(Expression source)
		{
			var gatherer = new SqlRedundantSubqueryFinder();

			gatherer.Visit(source);

			return gatherer.redundant;
		}

		protected static bool IsInitialProjection(SqlSelectExpression select)
		{
			return select.From is SqlTableExpression;
		}

		internal static bool IsSimpleProjection(SqlSelectExpression select)
		{
			foreach (var decl in select.Columns)
			{
				if (!(decl.Expression is SqlColumnExpression col) || decl.Name != col.Name)
				{
					return false;
				}
			}

			return true;
		}

		internal static bool IsNameMapProjection(SqlSelectExpression select)
		{
			if (select.From is SqlTableExpression)
			{
				return false;
			}

			if (!(@select.From is SqlSelectExpression fromSelect) || select.Columns.Count > fromSelect.Columns.Count)
			{
				return false;
			}

			var fromColumnNames = new HashSet<string>(fromSelect.Columns.Select(x => x.Name));

			foreach (var t in @select.Columns)
			{
				if (!(t.Expression is SqlColumnExpression columnExpression) || !fromColumnNames.Contains(columnExpression.Name))
				{
					return false;
				}
			}

			return true;
		}

		private static bool IsRedudantSubquery(SqlSelectExpression select)
		{
			return (IsSimpleProjection(select) || IsNameMapProjection(select))
				&& !select.Distinct
				&& (select.Take == null)
				&& (select.Skip == null)
				&& select.Where == null
				&& !select.Columns.Any(c => c.NoOptimise)
				&& ((select.OrderBy?.Count ?? 0) == 0)
				&& ((select.GroupBy?.Count ?? 0) == 0);
		}

		private readonly HashSet<Expression> ignoreSet = new HashSet<Expression>();

		protected override Expression VisitJoin(SqlJoinExpression join)
		{
			this.ignoreSet.Add(join.Left);
			this.ignoreSet.Add(join.Right);

			var left = Visit(join.Left);
			var right = Visit(join.Right);

			this.ignoreSet.Remove(join.Left);
			this.ignoreSet.Remove(join.Right);

			var condition = Visit(join.JoinCondition);

			if (left != join.Left || right != join.Right || condition != join.JoinCondition)
			{
				return new SqlJoinExpression(join.Type, join.JoinType, left, right, condition);
			}

			return join;
		}

		protected override Expression VisitUnion(SqlUnionExpression union)
		{
			return union;
		}

		protected override Expression VisitDelete(SqlDeleteExpression deleteExpression)
		{
			return deleteExpression;
		}

		protected override Expression VisitUpdate(SqlUpdateExpression updateExpression)
		{
			return updateExpression;
		}

		protected override Expression VisitInsertInto(SqlInsertIntoExpression insertIntoExpression)
		{
			return insertIntoExpression;
		}

		protected override Expression VisitSelect(SqlSelectExpression select)
		{
			if (this.ignoreSet.Contains(select))
			{
				return base.VisitSelect(select);
			}

			if (IsRedudantSubquery(select))
			{
				if (this.redundant == null)
				{
					this.redundant = new List<SqlSelectExpression>();
				}

				this.redundant.Add(select);
			}

			return base.VisitSelect(select);
		}

		protected override Expression VisitSubquery(SqlSubqueryExpression subquery)
		{
			return subquery;
		}
	}
}
