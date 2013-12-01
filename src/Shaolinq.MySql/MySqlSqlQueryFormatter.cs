﻿// Copyright (c) 2007-2013 Thong Nguyen (tumtumtum@gmail.com)

 using System.Collections.ObjectModel;
using System.Linq.Expressions;
﻿using Shaolinq.Persistence.Sql;
﻿using Shaolinq.Persistence.Sql.Linq;
using Shaolinq.Persistence.Sql.Linq.Expressions;

namespace Shaolinq.MySql
{
	public class MySqlSqlQueryFormatter
		: Sql92QueryFormatter
	{
		public DataAccessModel DataAccessModel { get; private set; }

		protected override char ParameterIndicatorChar
		{
			get
			{
				return '?';
			}
		}

		public MySqlSqlQueryFormatter(DataAccessModel dataAccessModel, SqlDataTypeProvider sqlDataTypeProvider, SqlDialect sqlDialect, Expression expression, SqlQueryFormatterOptions options)
			: base(expression, options, sqlDataTypeProvider, sqlDialect)
		{
			this.DataAccessModel = dataAccessModel;
		}

		protected override FunctionResolveResult ResolveSqlFunction(SqlFunction function, ReadOnlyCollection<Expression> arguments)
		{
			switch (function)
			{
				case SqlFunction.TrimLeft:
					return new FunctionResolveResult("LTRIM", false, arguments);
				case SqlFunction.TrimRight:
					return new FunctionResolveResult("RTRIM", false, arguments);
			}

			return base.ResolveSqlFunction(function, arguments);
		}

		protected override Expression VisitFunctionCall(SqlFunctionCallExpression functionCallExpression)
		{
			switch (functionCallExpression.Function)
			{
				case SqlFunction.DayOfWeek:
					commandText.Append("((");
					base.VisitFunctionCall(functionCallExpression);
					commandText.Append(") - 1)");

					return functionCallExpression;
			}

			return base.VisitFunctionCall(functionCallExpression);
		}
	}
}
