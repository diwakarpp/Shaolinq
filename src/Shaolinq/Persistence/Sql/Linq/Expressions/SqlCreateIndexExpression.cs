﻿using System.Linq.Expressions;

namespace Shaolinq.Persistence.Sql.Linq.Expressions
{
	public class SqlCreateIndexExpression
		: SqlBaseExpression
	{
		public override ExpressionType NodeType
		{
			get
			{
				return (ExpressionType)SqlExpressionType.CreateIndex;
			}
		}

		public SqlCreateIndexExpression()
			: base(typeof(void))
		{
		}
	}
}
