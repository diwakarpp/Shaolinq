﻿// Copyright (c) 2007-2013 Thong Nguyen (tumtumtum@gmail.com)

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Linq.Expressions;
using Shaolinq.Persistence.Linq.Expressions;

namespace Shaolinq.Persistence.Linq
{
	public class ObjectProjector
	{
		public SqlQueryFormatResult FormatResult { get; private set; }
		public DataAccessModel DataAccessModel { get; private set; }
		public SqlDatabaseContext SqlDatabaseContext { get; private set; }

		protected int count = 0;
		protected readonly IQueryProvider provider;
		protected SelectFirstType selectFirstType;
		protected readonly SqlAggregateType? sqlAggregateType;
		protected readonly bool isDefaultIfEmpty;
		protected readonly IRelatedDataAccessObjectContext relatedDataAccessObjectContext;

		public ObjectProjector(IQueryProvider provider, DataAccessModel dataAccessModel, SqlQueryFormatResult formatResult, SqlDatabaseContext sqlDatabaseContext, IRelatedDataAccessObjectContext relatedDataAccessObjectContext, SelectFirstType selectFirstType, SqlAggregateType? sqlAggregateType, bool isDefaultIfEmpty)
		{
			this.sqlAggregateType = sqlAggregateType;
			this.isDefaultIfEmpty = isDefaultIfEmpty;
			this.provider = provider;
			this.DataAccessModel = dataAccessModel;
			this.FormatResult = formatResult;
			this.SqlDatabaseContext = sqlDatabaseContext;
			this.selectFirstType = selectFirstType;
			this.relatedDataAccessObjectContext = relatedDataAccessObjectContext;
		}

		public virtual IEnumerable<T> ExecuteSubQuery<T>(LambdaExpression query)
		{
			var projection = (SqlProjectionExpression)ExpressionReplacer.Replace
				(
					query.Body,
					query.Parameters[0],
					Expression.Constant(this)
				);
		
			projection = (SqlProjectionExpression) Evaluator.PartialEval(this.DataAccessModel, projection, CanEvaluateLocally);
            
			var result = (IEnumerable<T>) this.provider.Execute(projection);
			var list = new List<T>(result);

			if (typeof (IQueryable<T>).IsAssignableFrom(query.Body.Type))
			{
				return list.AsQueryable();
			}

			return list;
		}

		private static bool CanEvaluateLocally(Expression expression)
		{
			if (expression.NodeType == ExpressionType.Parameter || (int)expression.NodeType > (int)SqlExpressionType.First)
			{
				return false;
			}

			return true;
		}
	}

	/// <summary>
	/// Base class for ObjectReaders that use Reflection.Emit
	/// </summary>
	/// <typeparam name="T">
	/// The type of objects this projector returns
	/// </typeparam>
	/// <typeparam name="U">
	/// The concrete type for types this projector returns.  This type
	/// parameter is usually the same as <see cref="U"/> unless <see cref="T"/>
	/// is a <see cref="DataAccessObject{OBJECT_TYPE}"/> type in which case <see cref="U"/>
	/// must inherit from <see cref="T"/> and is usually automatically generated
	/// by the TypeBuilding system using Reflection.Emit.
	/// </typeparam>
	public class ObjectProjector<T, U>
		: ObjectProjector, IEnumerable<T>
		where U : T
	{
		protected readonly object[] placeholderValues;
		protected readonly Func<ObjectProjector, IDataReader, object[], U> objectReader;

		public ObjectProjector(IQueryProvider provider, DataAccessModel dataAccessModel, SqlQueryFormatResult formatResult, SqlDatabaseContext sqlDatabaseContext, Delegate objectReader, IRelatedDataAccessObjectContext relatedDataAccessObjectContext, SelectFirstType selectFirstType, SqlAggregateType? sqlAggregateType, bool isDefaultIfEmpty, object[] placeholderValues)
			: base(provider, dataAccessModel, formatResult, sqlDatabaseContext, relatedDataAccessObjectContext, selectFirstType, sqlAggregateType, isDefaultIfEmpty)
		{
			this.placeholderValues = placeholderValues;
			this.objectReader = (Func<ObjectProjector, IDataReader, object[], U>)objectReader;
		}

		public virtual IEnumerator<T> GetEnumerator()
		{
			var transactionContext = this.DataAccessModel.AmbientTransactionManager.GetCurrentContext(false);

			using (var acquisition = transactionContext.AcquirePersistenceTransactionContext(this.SqlDatabaseContext))
			{
				var persistenceTransactionContext = (SqlDatabaseTransactionContext)acquisition.DatabaseTransactionContext;

				using (var dataReader = persistenceTransactionContext.ExecuteReader(this.FormatResult.CommandText, this.FormatResult.ParameterValues))
				{
					if (dataReader.Read())
					{
						if (this.isDefaultIfEmpty && this.sqlAggregateType != null)
						{
							if (dataReader.FieldCount > 0 && dataReader.IsDBNull(0))
							{
								yield break;
							}
						}
						
						if (this.sqlAggregateType != SqlAggregateType.Sum && this.sqlAggregateType != SqlAggregateType.Count && !typeof(T).IsNullableType())
						{
							if (dataReader.FieldCount > 0 && dataReader.IsDBNull(0))
							{
								throw new InvalidOperationException("Sequence contains no elements");
							}
						}
						
						if (this.isDefaultIfEmpty && this.sqlAggregateType == SqlAggregateType.Count)
						{
							if (dataReader.FieldCount > 0 && dataReader.GetInt64(0) == 0)
							{
								yield return (T)Convert.ChangeType(1, typeof(T));
							}
						}

						yield return this.objectReader(this, dataReader, placeholderValues);

						count++;
					}

					while (dataReader.Read())
					{
						yield return this.objectReader(this, dataReader, placeholderValues);

						count++;
					}
				}
			}
		}

		System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
		{
			return this.GetEnumerator();
		}
	}
}
