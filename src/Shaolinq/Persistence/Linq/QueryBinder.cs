﻿// Copyright (c) 2007-2018 Thong Nguyen (tumtumtum@gmail.com)

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Platform;
using Platform.Reflection;
using Shaolinq.Persistence.Linq.Expressions;
using Shaolinq.Persistence.Linq.Optimizers;
using Shaolinq.TypeBuilding;

namespace Shaolinq.Persistence.Linq
{
	public class QueryBinder
		: Platform.Linq.ExpressionVisitor
	{
		public DataAccessModel DataAccessModel { get; }

		private int aliasCount;
		private int aggregateCount;
		private Expression rootExpression;
		private readonly TypeDescriptorProvider typeDescriptorProvider;
		private readonly RelatedPropertiesJoinExpanderResults joinExpanderResults;
		private readonly Stack<Expression> selectorPredicateStack = new Stack<Expression>();
		private readonly Dictionary<Expression, GroupByInfo> groupByMap = new Dictionary<Expression, GroupByInfo>();
		private readonly Dictionary<ParameterExpression, Expression> expressionsByParameter = new Dictionary<ParameterExpression, Expression>();

		protected void AddExpressionByParameter(ParameterExpression parameterExpression, Expression expression)
		{
			this.expressionsByParameter[parameterExpression] = expression;
		}

		private QueryBinder(DataAccessModel dataAccessModel, Expression rootExpression, RelatedPropertiesJoinExpanderResults joinExpanderResults)
		{
			this.DataAccessModel = dataAccessModel;
			this.rootExpression = rootExpression;
			this.joinExpanderResults = joinExpanderResults;
			this.typeDescriptorProvider = dataAccessModel.TypeDescriptorProvider;
		}

		private int placeholderCount;

		public static Expression Bind(DataAccessModel dataAccessModel, Expression expression, ref int placeholderCount)
		{
			expression = SqlPredicateToWhereConverter.Convert(expression);
			expression = SqlPropertyAccessToSelectAmender.Amend(expression);
			expression = InterfaceAccessNormalizer.Normalize(dataAccessModel.TypeDescriptorProvider, expression);
			expression = SqlOrderByThenByCombiner.Combine(expression);
			expression = ShiftSubCollectionIncludesOutsideSkipTakeAmender.Amend(expression);
			expression = SqlIncludeExpander.Expand(expression);
			var joinExpanderResults = RelatedPropertiesJoinExpander.Expand(dataAccessModel, expression);

			expression = joinExpanderResults.ProcessedExpression;

			var queryBinder = new QueryBinder(dataAccessModel, expression, joinExpanderResults) { placeholderCount = placeholderCount };
			
			var retval = queryBinder.Visit(expression);

			placeholderCount = queryBinder.placeholderCount;

			return retval;
		}

		public static bool IsIntegralType(Expression expression)
		{
			return expression.Type.GetUnwrappedNullableType().IsIntegralType();
		}

		public static ColumnInfo[] GetPrimaryKeyColumnInfos(TypeDescriptorProvider typeDescriptorProvider, TypeDescriptor typeDescriptor)
		{
			return GetPrimaryKeyColumnInfos(typeDescriptorProvider, typeDescriptor, (c, d) => true, (c, d) => true);
		}

		public static ColumnInfo[] GetPrimaryKeyColumnInfos(TypeDescriptorProvider typeDescriptorProvider, TypeDescriptor typeDescriptor, Func<PropertyDescriptor, int, bool> follow, Func<PropertyDescriptor, int, bool> include)
		{
			return GetPrimaryKeyColumnInfos(typeDescriptorProvider, typeDescriptor, follow, include, new List<PropertyDescriptor>(0));
		}

		protected static ColumnInfo[] GetPrimaryKeyColumnInfos(TypeDescriptorProvider typeDescriptorProvider, TypeDescriptor typeDescriptor, Func<PropertyDescriptor, int, bool> follow, Func<PropertyDescriptor, int, bool> include, List<PropertyDescriptor> visitedProperties)
		{
			return GetColumnInfos(typeDescriptorProvider, typeDescriptor.PrimaryKeyProperties, follow, include, new List<PropertyDescriptor>(0));
		}

		public static ColumnInfo[] GetColumnInfos(TypeDescriptorProvider typeDescriptorProvider, IEnumerable<PropertyDescriptor> properties)
		{
			return GetColumnInfos(typeDescriptorProvider, properties, (c, d) => true, (c, d) => true, new List<PropertyDescriptor>(0));
		}

		public static ColumnInfo[] GetColumnInfos(TypeDescriptorProvider typeDescriptorProvider, params PropertyDescriptor[] properties)
		{
			return GetColumnInfos(typeDescriptorProvider, properties, (c, d) => true, (c, d) => true, new List<PropertyDescriptor>(0));
		}

		public static ColumnInfo[] GetColumnInfos(TypeDescriptorProvider typeDescriptorProvider, IEnumerable<PropertyDescriptor> properties, Func<PropertyDescriptor, int, bool> follow, Func<PropertyDescriptor, int, bool> include)
		{
			return GetColumnInfos(typeDescriptorProvider, properties, follow, include, new List<PropertyDescriptor>(0));
		}

		protected static ColumnInfo[] GetColumnInfos(TypeDescriptorProvider typeDescriptorProvider, IEnumerable<PropertyDescriptor> properties, Func<PropertyDescriptor, int, bool> follow, Func<PropertyDescriptor, int, bool> include, List<PropertyDescriptor> visitedProperties, int depth = 0)
		{
			var retval = new List<ColumnInfo>();

			foreach (var property in properties)
			{
				if (property.PropertyType.IsDataAccessObjectType())
				{
					if (!follow(property, depth))
					{
						continue;
					}

					var foreignTypeDescriptor = typeDescriptorProvider.GetTypeDescriptor(property.PropertyType);

					var newVisited = new List<PropertyDescriptor>(visitedProperties.Count + 1);

					newVisited.AddRange(visitedProperties);
					newVisited.Add(property);

					foreach (var relatedColumnInfo in GetColumnInfos(typeDescriptorProvider, foreignTypeDescriptor.PrimaryKeyProperties, follow, include, newVisited, depth + 1))
					{
						retval.Add(new ColumnInfo
						{
							RootProperty = property,
							ForeignType = foreignTypeDescriptor,
							DefinitionProperty = relatedColumnInfo.DefinitionProperty,
							VisitedProperties = relatedColumnInfo.VisitedProperties
						});
					}

				}
				else
				{
					if (!include(property, depth))
					{
						continue;
					}

					retval.Add(new ColumnInfo
					{
						RootProperty = property,
						ForeignType = null,
						DefinitionProperty = property,
						VisitedProperties = visitedProperties.ToArray()
					});
				}
			}

			return retval.ToArray();
		}

		private string GetNextAlias()
		{
			return "T" + (this.aliasCount++);
		}

		public static ProjectedColumns ProjectColumns(Expression expression, string newAlias, IEnumerable<SqlColumnDeclaration> existingColumns, params string[] existingAliases)
		{
			return ColumnProjector.ProjectColumns(new Nominator(Nominator.CanBeColumn, true), expression, existingColumns, newAlias, existingAliases);
		}

		private Expression BindCollectionContains(Expression list, Expression item, bool isRoot)
		{
			const string columnName = "CONTAINS";

			if (isRoot)
			{
				this.rootExpression = list;
			}

			var visitedList = Visit(list);

			if (visitedList is SqlConstantPlaceholderExpression visitedListAsConstant &&
				(typeof(IList<>).IsAssignableFromIgnoreGenericParameters(visitedListAsConstant.ConstantExpression.Type)
				|| typeof(ICollection<>).IsAssignableFromIgnoreGenericParameters(visitedListAsConstant.ConstantExpression.Type)
				|| typeof(IEnumerable).IsAssignableFrom(visitedListAsConstant.ConstantExpression.Type)
				|| typeof(IEnumerable<>).IsAssignableFromIgnoreGenericParameters(visitedListAsConstant.ConstantExpression.Type)))
			{
				visitedList = new SqlConstantPlaceholderExpression(visitedListAsConstant.Index, Expression.Constant(new SqlValuesEnumerable((IEnumerable)visitedListAsConstant.ConstantExpression.Value)));
			}

			var functionExpression = new SqlFunctionCallExpression(typeof(bool), SqlFunction.In, Visit(item), visitedList);

			if (this.selectorPredicateStack.Count > 0)
			{
				return functionExpression;
			}

			var alias = GetNextAlias();
			var selectType = typeof(IEnumerable<>).MakeGenericType(typeof(bool));

			var select = new SqlSelectExpression
			(
				selectType,
				alias,
				new[] { new SqlColumnDeclaration(columnName, functionExpression) },
				null,
				null,
				null,
				false
			);

			var retval = (Expression)new SqlProjectionExpression(select, new SqlColumnExpression(typeof(bool), alias, columnName), null, false, (visitedList as SqlProjectionExpression)?.DefaultValue);

			if (isRoot)
			{
				retval = GetSingletonSequence(retval, "SingleOrDefault");
			}

			return retval;
		}

		private Expression GetSingletonSequence(Expression expr, string aggregator)
		{
			var elementType = expr.Type.GetSequenceElementType() ?? expr.Type;

			var p = Expression.Parameter(typeof(IEnumerable<>).MakeGenericType(elementType), "p");
			LambdaExpression aggr = null;

			if (aggregator != null)
			{
				aggr = Expression.Lambda(Expression.Call(typeof(Enumerable), aggregator, new[] { elementType }, p), p);
			}

			var alias = GetNextAlias();
			var select = new SqlSelectExpression(expr.Type, alias, new[] { new SqlColumnDeclaration("value", expr) }, null, null, null);

			return new SqlProjectionExpression(select, new SqlColumnExpression(elementType, alias, "value"), aggr, false, (expr as SqlProjectionExpression)?.DefaultValue);
		}
		
		private Expression BindFirst(Expression source, LambdaExpression predicate, SelectFirstType selectFirstType, bool isRoot = false)
		{
			Expression where = null;
			var isDefaultIfEmpty = false;
			Expression defaultIfEmptyValue = null;

			if (isRoot)
			{
				isDefaultIfEmpty = source.TryStripDefaultIfEmptyCall(out source, out defaultIfEmptyValue);
			}

			var projection = VisitSequence(source);

			if (predicate != null)
			{
				this.expressionsByParameter[predicate.Parameters[0]] = projection.Projector;
				where = Visit(predicate.Body);
			}

			Expression take = null;

			var localSideAggregateEval = isRoot || this.selectorPredicateStack.Count > 0;
			var isFirst = selectFirstType == SelectFirstType.First || selectFirstType == SelectFirstType.FirstOrDefault;
			var isLast = selectFirstType == SelectFirstType.Last || selectFirstType == SelectFirstType.LastOrDefault;
			var isSingle = selectFirstType == SelectFirstType.Single || selectFirstType == SelectFirstType.SingleOrDefault;
			var isDaoWithIncludedProperties = ((source.Type.GetSequenceElementType()?.IsDataAccessObjectType() ?? false) && this.joinExpanderResults.IncludedPropertyInfos.Count > 0);

			if (!isDaoWithIncludedProperties)
			{
				take = (isFirst || isLast) ? Expression.Constant(1) : (isSingle ? (localSideAggregateEval ? Expression.Constant(2) : Expression.Constant(1)) : null);
			}

			if (take != null || where != null)
			{
				var alias = GetNextAlias();
				var pc = ProjectColumns(projection.Projector, alias, null, projection.Select.Alias);

				projection = new SqlProjectionExpression(new SqlSelectExpression(isRoot ? projection.Select.Type : projection.Select.Type.GetSequenceElementType(), alias, pc.Columns, projection.Select, where, null, null, false, null, take, projection.Select.ForUpdate, isLast), pc.Projector, null, false, projection.DefaultValue);
			}

			if (localSideAggregateEval)
			{
				LambdaExpression aggr;
				var elementType = projection.Projector.Type;
				var parameter = Expression.Parameter(typeof(IEnumerable<>).MakeGenericType(elementType));

				if (isDefaultIfEmpty)
				{
					if (defaultIfEmptyValue == null)
					{
						var defaultIfEmptyCall = Expression.Call(MethodInfoFastRef.EnumerableDefaultIfEmptyMethod.MakeGenericMethod(elementType), parameter);
						var firstCall = Expression.Call(typeof(Enumerable), selectFirstType.ToString(), new[] { elementType }, defaultIfEmptyCall);

						aggr = Expression.Lambda(firstCall, parameter);
					}
					else
					{
						var defaultIfEmptyCall = Expression.Call(MethodInfoFastRef.EnumerableDefaultIfEmptyWithValueMethod.MakeGenericMethod(elementType), parameter, Expression.Convert(defaultIfEmptyValue, elementType));
						var firstCall = Expression.Call(typeof(Enumerable), selectFirstType.ToString(), new[] { elementType }, defaultIfEmptyCall);

						aggr = Expression.Lambda(firstCall, parameter);
					}
				}
				else
				{
					aggr = Expression.Lambda(Expression.Call(typeof(Enumerable), selectFirstType.ToString(), new[] { elementType }, parameter), parameter);
				}

				return new SqlProjectionExpression(projection.Select, projection.Projector, aggr, false, projection.DefaultValue);
			}

			return projection;
		}

		private Expression BindAll(Expression source, LambdaExpression predicate, bool isRoot)
		{
			const string columnName = "EXISTS_COL";

			predicate = Expression.Lambda(Expression.Not(predicate.Body), predicate.Parameters);
			var projection = (SqlProjectionExpression)BindWhere(source.Type, source, predicate);

			if (isRoot)
			{
				this.rootExpression = projection;
			}

			var functionExpression = Expression.Not(new SqlFunctionCallExpression(typeof(bool), SqlFunction.Exists, Visit(projection)));

			if (this.selectorPredicateStack.Count > 0)
			{
				return functionExpression;
			}

			var alias = GetNextAlias();
			var selectType = typeof(IEnumerable<>).MakeGenericType(typeof(bool));

			var select = new SqlSelectExpression
			(
				selectType,
				alias,
				new[] { new SqlColumnDeclaration(columnName, functionExpression) },
				null,
				null,
				null,
				projection.Select.ForUpdate
			);

			var retval = (Expression)new SqlProjectionExpression(select, new SqlColumnExpression(typeof(bool), alias, columnName), null);

			if (isRoot)
			{
				retval = GetSingletonSequence(retval, "SingleOrDefault");
			}

			return retval;
		}

		private Expression BindAny(Expression source, bool isRoot)
		{
			const string columnName = "EXISTS_COL";

			if (isRoot)
			{
				this.rootExpression = source;
			}

			var projection = (SqlProjectionExpression)Visit(source);
			var functionExpression = new SqlFunctionCallExpression(typeof(bool), SqlFunction.Exists, projection);

			if (this.selectorPredicateStack.Count > 0)
			{
				return functionExpression;
			}

			var alias = GetNextAlias();
			var selectType = typeof(IEnumerable<>).MakeGenericType(typeof(bool));

			var select = new SqlSelectExpression
			(
				selectType,
				alias,
				new[] { new SqlColumnDeclaration(columnName, functionExpression) },
				null,
				null,
				null,
				projection.Select.ForUpdate
			);

			var retval = (Expression)new SqlProjectionExpression(select, new SqlColumnExpression(typeof(bool), alias, columnName), null);

			if (isRoot)
			{
				retval = GetSingletonSequence(retval, "SingleOrDefault");
			}

			return retval;
		}

		private Expression BindTake(Expression source, Expression take)
		{
			var projection = VisitSequence(source);

			take = Visit(take);

			var select = projection.Select;

			var alias = GetNextAlias();

			var pc = ProjectColumns(projection.Projector, alias, null, projection.Select.Alias);

			return new SqlProjectionExpression(new SqlSelectExpression(select.Type, alias, pc.Columns, projection.Select, null, null, null, false, null, take, select.ForUpdate), pc.Projector, null);
		}

		private Expression BindSkip(Expression source, Expression skip)
		{
			var projection = VisitSequence(source);

			skip = Visit(skip);

			var select = projection.Select;
			var alias = GetNextAlias();
			var pc = ProjectColumns(projection.Projector, alias, null, projection.Select.Alias);

			return new SqlProjectionExpression(new SqlSelectExpression(select.Type, alias, pc.Columns, projection.Select, null, null, null, false, skip, null, select.ForUpdate), pc.Projector, null);
		}

		protected override Expression VisitBinary(BinaryExpression binaryExpression)
		{
			Expression left, right;

			if (binaryExpression.Left.Type == typeof(string) && binaryExpression.Right.Type == typeof(string))
			{
				if (binaryExpression.NodeType == ExpressionType.Add)
				{
					left = Visit(binaryExpression.Left);
					right = Visit(binaryExpression.Right);

					return new SqlFunctionCallExpression(binaryExpression.Type, SqlFunction.Concat, left, right);
				}
			}

			if ((binaryExpression.NodeType == ExpressionType.GreaterThan
				|| binaryExpression.NodeType == ExpressionType.GreaterThanOrEqual
				|| binaryExpression.NodeType == ExpressionType.LessThan
				|| binaryExpression.NodeType == ExpressionType.LessThanOrEqual
				|| binaryExpression.NodeType == ExpressionType.Equal
				|| binaryExpression.NodeType == ExpressionType.NotEqual))
			{
				var methodCallExpressionLeft = binaryExpression.Left as MethodCallExpression;
				var methodCallExpressionRight = binaryExpression.Right as MethodCallExpression;
				var constantExpressionLeft = Visit(binaryExpression.Left) as ConstantExpression;
				var constantExpressionRight = Visit(binaryExpression.Right) as ConstantExpression;

				left = right = null;
				var operation = binaryExpression.NodeType;

				if (methodCallExpressionLeft?.Method?.Name == "CompareTo" && constantExpressionRight != null)
				{
					left = methodCallExpressionLeft.Object;
					right = methodCallExpressionLeft.Arguments[0];

					if (Convert.ToInt32(constantExpressionRight.Value) != 0)
					{
						throw new InvalidOperationException($"Result of CompareTo call requires an inequality comparison with 0 not {constantExpressionRight.Value}");
					}
				}
				else if (methodCallExpressionRight?.Method?.Name == "CompareTo" && constantExpressionLeft != null)
				{
					left = methodCallExpressionRight.Object;
					right = methodCallExpressionRight.Arguments[0];

					if (Convert.ToInt32(constantExpressionLeft.Value) != 0)
					{
						throw new InvalidOperationException($"Result of CompareTo call requires an inequality comparison with 0 not {constantExpressionLeft.Value}");
					}

					switch (operation)
					{
					case ExpressionType.GreaterThan:
						operation = ExpressionType.LessThanOrEqual;
						break;
					case ExpressionType.GreaterThanOrEqual:
						operation = ExpressionType.LessThan;
						break;
					case ExpressionType.LessThan:
						operation = ExpressionType.GreaterThanOrEqual;
						break;
					case ExpressionType.LessThanOrEqual:
						operation = ExpressionType.GreaterThan;
						break;
					}
				}

				if (left != null && right != null)
				{
					if (operation == ExpressionType.Equal || operation == ExpressionType.NotEqual)
					{
						return VisitBinary(Expression.MakeBinary(operation, left, right));
					}

					return new SqlFunctionCallExpression(typeof(bool), SqlFunction.CompareObject, Expression.Constant(operation), Visit(left), Visit(right));
				}
			}

			if (binaryExpression.NodeType == ExpressionType.Coalesce)
			{
				left = Visit(binaryExpression.Left);
				right = Visit(binaryExpression.Right);

				return new SqlFunctionCallExpression(binaryExpression.Type, SqlFunction.Coalesce, left, right);
			}

			left = Visit(binaryExpression.Left);
			right = Visit(binaryExpression.Right);

			var conversion = Visit(binaryExpression.Conversion);

			if (left != binaryExpression.Left || right != binaryExpression.Right || conversion != binaryExpression.Conversion)
			{
				if (binaryExpression.NodeType == ExpressionType.Coalesce)
				{
					return Expression.Coalesce(left, right, conversion as LambdaExpression);
				}

				if (left.NodeType == (ExpressionType)SqlExpressionType.ObjectReference && right.NodeType == (ExpressionType)SqlExpressionType.Projection)
				{
					var objectOperandExpression = (SqlObjectReferenceExpression)left;
					var tupleExpression = new SqlTupleExpression(objectOperandExpression.Bindings.OfType<MemberAssignment>().Select(c => c.Expression));
					var selector = MakeSelectorForPrimaryKeys(left.Type, tupleExpression.Type);
					var rightWithSelect = BindSelectForPrimaryKeyProjection(tupleExpression.Type, (SqlProjectionExpression)right, selector, false);

					return Expression.MakeBinary(binaryExpression.NodeType, tupleExpression, rightWithSelect, binaryExpression.IsLiftedToNull, binaryExpression.Method);
				}
				else if (left.NodeType == (ExpressionType)SqlExpressionType.Projection && right.NodeType == (ExpressionType)SqlExpressionType.ObjectReference)
				{
					var objectOperandExpression = (SqlObjectReferenceExpression)right;
					var tupleExpression = new SqlTupleExpression(objectOperandExpression.Bindings.OfType<MemberAssignment>().Select(c => c.Expression));
					var selector = MakeSelectorForPrimaryKeys(right.Type, tupleExpression.Type);
					var leftWithSelect = BindSelectForPrimaryKeyProjection(tupleExpression.Type, (SqlProjectionExpression)left, selector, false);

					return Expression.MakeBinary(binaryExpression.NodeType, leftWithSelect, tupleExpression, binaryExpression.IsLiftedToNull, binaryExpression.Method);
				}
				else
				{
					return Expression.MakeBinary(binaryExpression.NodeType, left, right, binaryExpression.IsLiftedToNull, binaryExpression.Method);
				}
			}

			return binaryExpression;
		}

		private LambdaExpression MakeSelectorForPrimaryKeys(Type objectType, Type returnType)
		{
			var parameter = Expression.Parameter(objectType);
			var newExpression = Expression.New(returnType);

			var bindings = new List<MemberBinding>();
			var typeDescriptor = this.typeDescriptorProvider.GetTypeDescriptor(objectType);

			var itemNumber = 1;

			foreach (var property in typeDescriptor.PrimaryKeyProperties)
			{
				var itemProperty = returnType.GetProperty("Item" + itemNumber, BindingFlags.Instance | BindingFlags.Public);
				bindings.Add(Expression.Bind(itemProperty, Expression.Property(parameter, property.PropertyName)));

				itemNumber++;
			}

			var body = Expression.MemberInit(newExpression, bindings);

			return Expression.Lambda(body, parameter);
		}

		private Expression BindSelectForPrimaryKeyProjection(Type resultType, SqlProjectionExpression projection, LambdaExpression selector, bool forUpdate)
		{
			AddExpressionByParameter(selector.Parameters[0], projection.Projector);

			var expression = Visit(selector.Body);
			var alias = GetNextAlias();
			var pc = ProjectColumns(expression, alias, null, projection.Select.Alias);

			return new SqlProjectionExpression(new SqlSelectExpression(resultType, alias, pc.Columns, projection.Select, null, null, null, false, null, null, forUpdate || projection.Select.ForUpdate), pc.Projector, null);
		}

		protected virtual Expression BindDefaultIfEmpty(Type resultType, Expression source, Expression value, bool isRoot)
		{
			if (value != null && !isRoot)
			{
				throw new NotSupportedException($"{nameof(Queryable.DefaultIfEmpty)} is not supported in this context");
			}

			if (value == null)
			{
				var projection = VisitSequence(source);
				var leftSelect = new SqlSelectExpression(resultType, GetNextAlias(), new[] { new SqlColumnDeclaration("__SHAOLINQ__EMPTY", Expression.Constant(null), true) }, null, null, null);

				var join = new SqlJoinExpression(resultType, SqlJoinType.Left, leftSelect, projection.Select, Expression.Constant(true));
				var alias = GetNextAlias();

				var projected = ProjectColumns(projection.Projector, alias, null, leftSelect.Alias, projection.Select.Alias);

				return new SqlProjectionExpression(new SqlSelectExpression(resultType, alias, projected.Columns, join, null, null, null, false, null, null, projection.Select.ForUpdate), projected.Projector, null, false); 
			}
			else
			{
				var projection = VisitSequence(source);
				var parameter = Expression.Parameter(source.Type);

				var aggregator = Expression.Lambda
				(
					Expression.Call
					(
						MethodInfoFastRef.EnumerableDefaultIfEmptyWithValueMethod.MakeGenericMethod(resultType.GetSequenceElementType().GetUnwrappedNullableType()),
						parameter,
						value
					),
					parameter
				);

				return new SqlProjectionExpression(projection.Select, projection.Projector, aggregator);
			}
		}

		protected virtual Expression BindJoin(Type resultType, Expression outerSource, Expression innerSource, LambdaExpression outerKey, LambdaExpression innerKey, LambdaExpression resultSelector, SqlJoinType joinType = SqlJoinType.Inner)
		{
			var outerProjection = VisitSequence(outerSource);
			var innerProjection = VisitSequence(innerSource);

			AddExpressionByParameter(outerKey.Parameters[0], outerProjection.Projector);
			var outerKeyExpr = Visit(outerKey.Body).StripObjectBindingCalls();
			AddExpressionByParameter(innerKey.Parameters[0], innerProjection.Projector);
			var innerKeyExpression = Visit(innerKey.Body).StripObjectBindingCalls();

			AddExpressionByParameter(resultSelector.Parameters[0], outerProjection.Projector);
			AddExpressionByParameter(resultSelector.Parameters[1], innerProjection.Projector);

			var resultExpr = Visit(resultSelector.Body);

			var join = new SqlJoinExpression(resultType, joinType, outerProjection.Select, innerProjection.Select, Expression.Equal(outerKeyExpr, innerKeyExpression));

			var alias = GetNextAlias();

			var projectedColumns = ProjectColumns(resultExpr, alias, null, outerProjection.Select.Alias, innerProjection.Select.Alias);

			return new SqlProjectionExpression(new SqlSelectExpression(resultType, alias, projectedColumns.Columns, join, null, null, outerProjection.Select.ForUpdate || innerProjection.Select.ForUpdate), projectedColumns.Projector, null);
		}

		protected virtual Expression BindSelectMany(Type resultType, Expression source, LambdaExpression collectionSelector, LambdaExpression resultSelector)
		{
			ProjectedColumns projectedColumns;
			var projection = VisitSequence(source);
			AddExpressionByParameter(collectionSelector.Parameters[0], projection.Projector);

			var defaultIfEmpty = false;

			if (collectionSelector.Body.TryStripDefaultIfEmptyCalls(out var collection))
			{
				defaultIfEmpty = true;
			}
			else
			{
				collection = collectionSelector.Body;
			}

			var collectionProjection = VisitSequence(collection);

			if (collectionProjection.Select.From is SqlTableExpression && defaultIfEmpty)
			{
				collection = collectionSelector.Body;
				collectionProjection = VisitSequence(collection);
			}

			var isTable = collectionProjection.Select.From is SqlTableExpression;
			var joinType = defaultIfEmpty ? SqlJoinType.OuterApply : (isTable ? SqlJoinType.Cross : SqlJoinType.CrossApply);
			var join = new SqlJoinExpression(resultType, joinType, projection.Select, collectionProjection.Select, null);

			var alias = GetNextAlias();

			if (resultSelector == null)
			{
				projectedColumns = ProjectColumns(collectionProjection.Projector, alias, null, projection.Select.Alias, collectionProjection.Select.Alias);
			}
			else
			{
				AddExpressionByParameter(resultSelector.Parameters[0], projection.Projector);
				AddExpressionByParameter(resultSelector.Parameters[1], collectionProjection.Projector);

				var resultExpression = Visit(resultSelector.Body);

				projectedColumns = ProjectColumns(resultExpression, alias, null, projection.Select.Alias, collectionProjection.Select.Alias);
			}

			return new SqlProjectionExpression(new SqlSelectExpression(resultType, alias, projectedColumns.Columns, join, null, null, projection.Select.ForUpdate), projectedColumns.Projector, null);
		}

		protected virtual Expression BindGroupBy(Expression source, LambdaExpression keySelector, LambdaExpression elementSelector, LambdaExpression resultSelector)
		{
			var projection = VisitSequence(source);

			AddExpressionByParameter(keySelector.Parameters[0], projection.Projector);

			var keyExpression = Visit(keySelector.Body);

			var elementExpression = projection.Projector;

			if (elementSelector != null)
			{
				AddExpressionByParameter(elementSelector.Parameters[0], projection.Projector);
				elementExpression = Visit(elementSelector.Body);
			}

			// Use ProjectColumns to get group-by expressions from key expression
			var keyProjection = ProjectColumns(keyExpression, projection.Select.Alias, null, projection.Select.Alias);

			// Make duplicate of source query as basis of element subquery by visiting the source again
			var subqueryBasis = VisitSequence(source);

			// Recompute key columns for group expressions relative to subquery (need these for doing the correlation predicate)
			AddExpressionByParameter(keySelector.Parameters[0], subqueryBasis.Projector);
			var subqueryKey = Visit(keySelector.Body);

			// Use same projection trick to get group by expressions based on subquery

			var subQueryProjectedColumns = ProjectColumns(subqueryKey, subqueryBasis.Select.Alias, null, subqueryBasis.Select.Alias);

			var groupExprs = keyProjection.Columns.Select(c => c.Expression).ToArray();
			var subqueryGroupExprs = subQueryProjectedColumns.Columns.Select(c => c.Expression).ToArray();

			var subqueryCorrelation = BuildPredicateWithNullsEqual(subqueryGroupExprs, groupExprs);

			// Compute element based on duplicated subquery
			var subqueryElemExpr = subqueryBasis.Projector;

			if (elementSelector != null)
			{
				AddExpressionByParameter(elementSelector.Parameters[0], subqueryBasis.Projector);
				subqueryElemExpr = Visit(elementSelector.Body);
			}

			// Build subquery that projects the desired element

			var elementAlias = GetNextAlias();

			var elementProjectedColumns = ProjectColumns(subqueryElemExpr, elementAlias, null, subqueryBasis.Select.Alias);

			var elementSubquery = new SqlProjectionExpression
			(
				new SqlSelectExpression
				(
					TypeHelper.GetQueryableSequenceType(subqueryElemExpr.Type),
					elementAlias,
					elementProjectedColumns.Columns,
					subqueryBasis.Select,
					subqueryCorrelation,
					null,
					subqueryBasis.Select.ForUpdate
				),
				elementProjectedColumns.Projector,
				null
			);

			var alias = GetNextAlias();
			var info = new GroupByInfo(alias, elementExpression);

			this.groupByMap.Add(elementSubquery, info);

			Expression resultExpression;

			if (resultSelector != null)
			{
				var saveGroupElement = this.currentGroupElement;

				this.currentGroupElement = elementSubquery;

				AddExpressionByParameter(resultSelector.Parameters[0], keyProjection.Projector);
				AddExpressionByParameter(resultSelector.Parameters[1], elementSubquery);

				resultExpression = Visit(resultSelector.Body);

				this.currentGroupElement = saveGroupElement;
			}
			else
			{
				var groupingType = typeof(Grouping<,>).MakeGenericType(keyExpression.Type, subqueryElemExpr.Type);

				resultExpression = Expression.New
				(
					groupingType.GetConstructors()[0],
					new[] { keyExpression, elementSubquery },
					groupingType.GetProperty("Key", BindingFlags.Instance | BindingFlags.Public),
					groupingType.GetProperty("Group", BindingFlags.Instance | BindingFlags.Public)
				);
			}

			var pc = ProjectColumns(resultExpression, alias, null, projection.Select.Alias);
			var projectedElementSubquery = ((NewExpression)pc.Projector).Arguments[1];

			if (projectedElementSubquery != elementSubquery)
			{
				this.groupByMap.Add(projectedElementSubquery, info);
			}

			return new SqlProjectionExpression
			(
				new SqlSelectExpression
				(
					TypeHelper.GetQueryableSequenceType(resultExpression.Type),
					alias,
					pc.Columns,
					projection.Select,
					null,
					null,
					groupExprs,
					false, null, null, projection.Select.ForUpdate
				),
				pc.Projector,
				null
			);
		}

		private static Expression BuildPredicateWithNullsEqual(IEnumerable<Expression> source1, IEnumerable<Expression> source2)
		{
			var enumerator1 = source1.GetEnumerator();
			var enumerator2 = source2.GetEnumerator();

			Expression result = null;

			while (enumerator1.MoveNext() && enumerator2.MoveNext())
			{
				var compare = Expression.Or
				(
					Expression.And
					(
						new SqlFunctionCallExpression(typeof(bool), SqlFunction.IsNull, enumerator1.Current),
						new SqlFunctionCallExpression(typeof(bool), SqlFunction.IsNull, enumerator2.Current)
					),
					Expression.Equal(enumerator1.Current, enumerator2.Current)
				);

				result = (result == null) ? compare : Expression.And(result, compare);
			}

			return result;
		}

		protected virtual Expression BindGroupJoin(MethodInfo groupJoinMethod, Expression outerSource, Expression innerSource, LambdaExpression outerKey, LambdaExpression innerKey, LambdaExpression resultSelector)
		{
			var args = groupJoinMethod.GetGenericArguments();

			var outerProjection = VisitSequence(outerSource);

			AddExpressionByParameter(outerKey.Parameters[0], outerProjection.Projector);
			var predicateLambda = Expression.Lambda(Expression.Equal(innerKey.Body, outerKey.Body), innerKey.Parameters[0]);
			var callToWhere = Expression.Call(MethodInfoFastRef.EnumerableWhereMethod.MakeGenericMethod(args[1]), innerSource, predicateLambda);
			var group = Visit(callToWhere);

			AddExpressionByParameter(resultSelector.Parameters[0], outerProjection.Projector);
			AddExpressionByParameter(resultSelector.Parameters[1], group);

			var resultExpr = Visit(resultSelector.Body);

			var alias = GetNextAlias();
			var pc = ProjectColumns(resultExpr, alias, null, outerProjection.Select.Alias);

			return new SqlProjectionExpression(new SqlSelectExpression(outerProjection.Select.Type, alias, pc.Columns, outerProjection.Select, null, null, outerProjection.Select.ForUpdate), pc.Projector, null);
		}

		public static LambdaExpression GetLambda(Expression e)
		{
			while (e.NodeType == ExpressionType.Quote)
			{
				e = ((UnaryExpression)e).Operand;
			}

			if (e.NodeType == ExpressionType.Constant)
			{
				return ((ConstantExpression)e).Value as LambdaExpression;
			}

			return e as LambdaExpression;
		}

		protected override Expression VisitMethodCall(MethodCallExpression methodCallExpression)
		{
			if (methodCallExpression.Method.DeclaringType == typeof(Queryable)
				|| methodCallExpression.Method.DeclaringType == typeof(Enumerable)
				|| methodCallExpression.Method.DeclaringType == typeof(QueryableExtensions))
			{
				Expression result;

				switch (methodCallExpression.Method.Name)
				{
				case "Where":
					this.selectorPredicateStack.Push(methodCallExpression);
					result = BindWhere(methodCallExpression.Type, methodCallExpression.Arguments[0], methodCallExpression.Arguments[1].StripQuotes());
					this.selectorPredicateStack.Pop();
					return result;
				case "Select":
					this.selectorPredicateStack.Push(methodCallExpression);
					result = BindSelect(methodCallExpression.Type, methodCallExpression.Arguments[0], methodCallExpression.Arguments[1].StripQuotes(), false);
					this.selectorPredicateStack.Pop();
					return result;
				case "UpdateHelper":
					result = BindUpdateHelper(methodCallExpression.Arguments[0], methodCallExpression.Arguments[1].StripQuotes(), (bool)methodCallExpression.Arguments[2].StripAndGetConstant().Value);
					return result;
				case "InsertHelper":
					result = BindInsertHelper(methodCallExpression.Arguments[0], methodCallExpression.Arguments[1].StripQuotes(), (bool)methodCallExpression.Arguments[2].StripAndGetConstant().Value);
					return result;
				case "CreateIndexHelper":
					result = BindInsertHelper(methodCallExpression.Arguments[0], methodCallExpression.Arguments[1].StripQuotes(), (bool)methodCallExpression.Arguments[2].StripAndGetConstant().Value);
					return result;
				case "ForUpdate":
					return BindForUpdate(methodCallExpression.Arguments[0]);
				case "OrderBy":
					this.selectorPredicateStack.Push(methodCallExpression);
					result = BindOrderBy(methodCallExpression.Type, methodCallExpression.Arguments[0], methodCallExpression.Arguments[1].StripQuotes(), SortOrder.Ascending);
					this.selectorPredicateStack.Pop();
					return result;
				case "OrderByDescending":
					this.selectorPredicateStack.Push(methodCallExpression);
					result = BindOrderBy(methodCallExpression.Type, methodCallExpression.Arguments[0], methodCallExpression.Arguments[1].StripQuotes(), SortOrder.Descending);
					this.selectorPredicateStack.Pop();
					return result;
				case "OrderByThenBysHelper":
					this.selectorPredicateStack.Push(methodCallExpression);
					result = BindOrderByThenBys(methodCallExpression.Type, methodCallExpression.Arguments[0], (LambdaExpression[])methodCallExpression.Arguments[1].StripAndGetConstant().Value, (SortOrder[])methodCallExpression.Arguments[2].StripAndGetConstant().Value);
					this.selectorPredicateStack.Pop();
					return result;
				case "GroupJoin":
					if (methodCallExpression.Method.IsGenericMethod && methodCallExpression.Method.GetGenericMethodDefinition() == MethodInfoFastRef.QueryableGroupJoinMethod)
					{
						return BindGroupJoin(methodCallExpression.Method, methodCallExpression.Arguments[0], methodCallExpression.Arguments[1], GetLambda(methodCallExpression.Arguments[2]), GetLambda(methodCallExpression.Arguments[3]), GetLambda(methodCallExpression.Arguments[4]));
					}
					break;
				case "GroupBy":
					this.selectorPredicateStack.Push(methodCallExpression);
					if (methodCallExpression.Arguments.Count == 2)
					{
						result = BindGroupBy
						(
							methodCallExpression.Arguments[0],
							methodCallExpression.Arguments[1].StripQuotes(),
							null,
							null
						);
					}
					else if (methodCallExpression.Arguments.Count == 3)
					{
						result = BindGroupBy
						(
							methodCallExpression.Arguments[0],
							methodCallExpression.Arguments[1].StripQuotes(),
							methodCallExpression.Arguments[2].StripQuotes(),
							null
						);
					}
					else if (methodCallExpression.Arguments.Count == 4)
					{
						result = BindGroupBy
						(
							methodCallExpression.Arguments[0],
							methodCallExpression.Arguments[1].StripQuotes(),
							methodCallExpression.Arguments[2].StripQuotes(),
							methodCallExpression.Arguments[3].StripQuotes()
						);
					}
					else
					{
						break;
					}
					this.selectorPredicateStack.Pop();
					return result;
				case "Any":
					return BindAny(methodCallExpression.Arguments[0], methodCallExpression == this.rootExpression);
				case "All":
					return BindAll(methodCallExpression.Arguments[0], methodCallExpression.Arguments[1].StripQuotes(), methodCallExpression == this.rootExpression);
				case "Count":
				case "LongCount":
				case "Min":
				case "Max":
				case "Sum":
				case "Average":
					if (methodCallExpression.Arguments.Count == 1)
					{
						return BindAggregate(methodCallExpression.Arguments[0], methodCallExpression.Method, null, methodCallExpression == this.rootExpression);
					}
					else if (methodCallExpression.Arguments.Count == 2)
					{
						var selector = methodCallExpression.Arguments[1].StripQuotes();

						return BindAggregate(methodCallExpression.Arguments[0], methodCallExpression.Method, selector, methodCallExpression == this.rootExpression);
					}
					break;
				case "Distinct":
					return BindDistinct(methodCallExpression.Type, methodCallExpression.Arguments[0]);
				case "Join":
					return BindJoin(methodCallExpression.Type,
						methodCallExpression.Arguments[0],
						methodCallExpression.Arguments[1],
						methodCallExpression.Arguments[2].StripQuotes(),
						methodCallExpression.Arguments[3].StripQuotes(),
						methodCallExpression.Arguments[4].StripQuotes());
				case "SelectMany":
					this.selectorPredicateStack.Push(methodCallExpression);
					if (methodCallExpression.Arguments.Count == 2)
					{
						result = BindSelectMany(methodCallExpression.Type, methodCallExpression.Arguments[0], methodCallExpression.Arguments[1].StripQuotes(), null);
					}
					else if (methodCallExpression.Arguments.Count == 3)
					{
						result = BindSelectMany(methodCallExpression.Type, methodCallExpression.Arguments[0], methodCallExpression.Arguments[1].StripQuotes(), methodCallExpression.Arguments[2].StripQuotes());
					}
					else
					{
						this.selectorPredicateStack.Pop();
						break;
					}
					this.selectorPredicateStack.Pop();
					return result;
				case "Skip":
					if (methodCallExpression.Arguments.Count == 2)
					{
						return BindSkip(methodCallExpression.Arguments[0], methodCallExpression.Arguments[1]);
					}
					break;
				case "Take":
					if (methodCallExpression.Arguments.Count == 2)
					{
						return BindTake(methodCallExpression.Arguments[0], methodCallExpression.Arguments[1]);
					}
					break;
				case "First":
				case "FirstOrDefault":
				case "Single":
				case "SingleOrDefault":
					if (methodCallExpression.Arguments.Count == 1)
					{
						return BindFirst(methodCallExpression.Arguments[0], null, (SelectFirstType)Enum.Parse(typeof(SelectFirstType), methodCallExpression.Method.Name), methodCallExpression == this.rootExpression);
					}
					else if (methodCallExpression.Arguments.Count == 2)
					{
						return BindFirst(methodCallExpression.Arguments[0], GetLambda(methodCallExpression.Arguments[1]), (SelectFirstType)Enum.Parse(typeof(SelectFirstType), methodCallExpression.Method.Name), methodCallExpression == this.rootExpression);
					}
					break;

				case "DefaultIfEmpty":
					if (methodCallExpression.Arguments.Count == 1)
					{
						return BindDefaultIfEmpty(methodCallExpression.Type, methodCallExpression.Arguments[0], null, methodCallExpression == this.rootExpression);
					}
					else if (methodCallExpression.Arguments.Count == 2)
					{
						return BindDefaultIfEmpty(methodCallExpression.Type, methodCallExpression.Arguments[0], methodCallExpression.Arguments[1], methodCallExpression == this.rootExpression);
					}
					else
					{
						throw new NotSupportedException(methodCallExpression.ToString());
					}
				case "Contains":
					if (methodCallExpression.Arguments.Count == 2)
					{
						return BindCollectionContains(methodCallExpression.Arguments[0], methodCallExpression.Arguments[1], methodCallExpression == this.rootExpression);
					}
					break;
				case "Delete":
					if (methodCallExpression.Arguments.Count == 1)
					{
						return BindDelete(methodCallExpression.Arguments[0]);
					}
					break;
				case "Union":
					if (methodCallExpression.Arguments.Count == 2)
					{
						return BindUnion(methodCallExpression.Type, methodCallExpression.Arguments[0], methodCallExpression.Arguments[1], false);
					}
					break;
				case "Concat":
					if (methodCallExpression.Arguments.Count == 2)
					{
						return BindUnion(methodCallExpression.Type, methodCallExpression.Arguments[0], methodCallExpression.Arguments[1], true);
					}
					break;
				case "AsEnumerable":
					return Visit(methodCallExpression.Arguments[0]);
				}

				throw new NotSupportedException($"Linq function \"{methodCallExpression.Method.Name}\" is not supported");
			}

			if (methodCallExpression.Method.DeclaringType.GetUnwrappedNullableType() == typeof(DateTime))
			{
				switch (methodCallExpression.Method.Name)
				{
				case "AddMilliseconds":
					return new SqlFunctionCallExpression(typeof(DateTime), SqlFunction.DateTimeAddTimeSpan, Visit(methodCallExpression.Object), new SqlFunctionCallExpression(typeof(TimeSpan), SqlFunction.TimeSpanFromSeconds, Visit(Expression.Divide(Expression.Convert(methodCallExpression.Arguments[0], typeof(double)), Expression.Constant(1000.0, typeof(double))))));
				case "AddSeconds":
					return new SqlFunctionCallExpression(typeof(DateTime), SqlFunction.DateTimeAddTimeSpan, Visit(methodCallExpression.Object), new SqlFunctionCallExpression(typeof(TimeSpan), SqlFunction.TimeSpanFromSeconds, VisitExpressionList(methodCallExpression.Arguments)));
				case "AddMinutes":
					return new SqlFunctionCallExpression(typeof(DateTime), SqlFunction.DateTimeAddTimeSpan, Visit(methodCallExpression.Object), new SqlFunctionCallExpression(typeof(TimeSpan), SqlFunction.TimeSpanFromMinutes, VisitExpressionList(methodCallExpression.Arguments)));
				case "AddHours":
					return new SqlFunctionCallExpression(typeof(DateTime), SqlFunction.DateTimeAddTimeSpan, Visit(methodCallExpression.Object), new SqlFunctionCallExpression(typeof(TimeSpan), SqlFunction.TimeSpanFromHours, VisitExpressionList(methodCallExpression.Arguments)));
				case "AddDays":
					return new SqlFunctionCallExpression(typeof(DateTime), SqlFunction.DateTimeAddDays, VisitExpressionList(methodCallExpression.Arguments).Prepend(Visit(methodCallExpression.Object)));
				case "AddMonths":
					return new SqlFunctionCallExpression(typeof(DateTime), SqlFunction.DateTimeAddMonths, VisitExpressionList(methodCallExpression.Arguments).Prepend(Visit(methodCallExpression.Object)));
				case "AddYears":
					return new SqlFunctionCallExpression(typeof(DateTime), SqlFunction.DateTimeAddYears, VisitExpressionList(methodCallExpression.Arguments).Prepend(Visit(methodCallExpression.Object)));
				}
			}

			if (methodCallExpression.Method.GetGenericMethodOrRegular() == MethodInfoFastRef.DataAccessObjectExtensionsAddToCollectionMethod)
			{
				return base.VisitMethodCall(methodCallExpression);
			}

			if (typeof(IList).IsAssignableFrom(methodCallExpression.Method.DeclaringType)
				|| typeof(ICollection).IsAssignableFrom(methodCallExpression.Method.DeclaringType)
				|| typeof(ICollection<>).IsAssignableFromIgnoreGenericParameters(methodCallExpression.Method.DeclaringType))
			{
				switch (methodCallExpression.Method.Name)
				{
				case "Contains":
					if (methodCallExpression.Arguments.Count == 1)
					{
						return BindCollectionContains(methodCallExpression.Object, methodCallExpression.Arguments[0], methodCallExpression == this.rootExpression);
					}
					break;
				}
			}
			else if (methodCallExpression.Method == MethodInfoFastRef.StringExtensionsIsLikeMethod)
			{
				var operand1 = Visit(methodCallExpression.Arguments[0]);
				var operand2 = Visit(methodCallExpression.Arguments[1]);

				return new SqlFunctionCallExpression(typeof(bool), SqlFunction.Like, operand1, operand2);
			}
			else if (methodCallExpression.Method.GetGenericMethodOrRegular() == MethodInfoFastRef.QueryableExtensionsLeftJoinMethod)
			{
				return BindJoin(methodCallExpression.Type, methodCallExpression.Arguments[0],
					methodCallExpression.Arguments[1],
					methodCallExpression.Arguments[2].StripQuotes(),
					methodCallExpression.Arguments[3].StripQuotes(),
					methodCallExpression.Arguments[4].StripQuotes(),
					SqlJoinType.Left);
			}
			else if (methodCallExpression.Method.DeclaringType == typeof(string))
			{
				return BindStringMethod(methodCallExpression);
			}
			else if (methodCallExpression.Method == MethodInfoFastRef.ObjectEqualsMethod
					 || methodCallExpression.Method.Name == "Equals" && methodCallExpression.Method.ReturnType == typeof(bool) && methodCallExpression.Arguments.Count == 1)
			{
				return VisitBinary(Expression.Equal(methodCallExpression.Object, methodCallExpression.Arguments[0]));
			}
			else if (methodCallExpression.Method == MethodInfoFastRef.ObjectStaticEqualsMethod || methodCallExpression.Method == MethodInfoFastRef.ObjectStaticReferenceEqualsMethod)
			{
				return VisitBinary(Expression.Equal(methodCallExpression.Arguments[0], methodCallExpression.Arguments[1]));
			}

			return base.VisitMethodCall(methodCallExpression);
		}

		private Expression BindUpdateHelper(Expression source, LambdaExpression updatedValues, bool requiresIdentityInsert)
		{
			var updatedValueExpressions = ((BlockExpression)updatedValues.Body).Expressions;
			var assignments = new List<Expression>();

			var projection = (SqlProjectionExpression)Visit(source);
			
			AddExpressionByParameter(updatedValues.Parameters[0], source);

			var alias = GetNextAlias();

			foreach (var updated in updatedValueExpressions)
			{
				var assignment = updated as MethodCallExpression;
				var propertyType = assignment.Method.GetGenericArguments()[1];
				var persistedName = assignment.Arguments[1].StripAndGetConstant().Value as string;
				var value = assignment.Arguments[2];

				assignments.Add(new SqlAssignExpression(new SqlColumnExpression(propertyType, null, persistedName), Visit(value)));
			}
			
			var update = new SqlUpdateExpression(projection, assignments.ToReadOnlyCollection(), null, requiresIdentityInsert);
			var select = new SqlSelectExpression(typeof(int), alias, new[] { new SqlColumnDeclaration("__SHAOLINQ__UPDATE", Expression.Constant(null), true) }, update, null, null, projection.Select.ForUpdate);

			var parameterExpression = Expression.Parameter(typeof(IEnumerable<int>));

			var aggregator = Expression.Lambda
			(
				Expression.Call(MethodInfoFastRef.EnumerableExtensionsAlwaysReadFirstMethod.MakeGenericMethod(typeof(int)), parameterExpression),
				parameterExpression
			);

			return new SqlProjectionExpression(select, new SqlFunctionCallExpression(typeof(int), SqlFunction.RecordsAffected), aggregator, false);
		}

		private Expression BindInsertHelper(Expression source, LambdaExpression updatedValues, bool requiresIdentityInsert)
		{
			var updatedValueExpressions = ((BlockExpression)updatedValues.Body).Expressions;
			var values = new List<Expression>();
			var columnNames = new List<string>();

			var projection = (SqlProjectionExpression)Visit(source);

			AddExpressionByParameter(updatedValues.Parameters[0], source);

			var alias = GetNextAlias();

			foreach (var updated in updatedValueExpressions)
			{
				var assignment = updated as MethodCallExpression;
				var persistedName = assignment.Arguments[1].StripAndGetConstant().Value as string;
				var value = assignment.Arguments[2];

				columnNames.Add(persistedName);

				values.Add(Visit(value));
			}

			var typeDescriptor = this.typeDescriptorProvider.GetTypeDescriptor(source.Type.GetSequenceElementType());

			var propertyDescriptors = typeDescriptor.PersistedPropertiesWithoutBackreferences.Where(c => c.IsPropertyThatIsCreatedOnTheServerSide).ToList();
			var returningAutoIncrementColumnNames = propertyDescriptors.Select(c => c.PersistedName).ToReadOnlyCollection();

			var insert = new SqlInsertIntoExpression(projection, columnNames, returningAutoIncrementColumnNames, values.ToReadOnlyCollection(), null, requiresIdentityInsert);
			var select = new SqlSelectExpression(typeof(int), alias, new[] { new SqlColumnDeclaration("__SHAOLINQ__INSERT", Expression.Constant(null), true) }, insert, null, null, projection.Select.ForUpdate);

			var parameterExpression = Expression.Parameter(typeof(IEnumerable<int>));

			var aggregator = Expression.Lambda
			(
				Expression.Call(MethodInfoFastRef.EnumerableExtensionsAlwaysReadFirstMethod.MakeGenericMethod(typeof(int)), parameterExpression),
				parameterExpression
			);

			return new SqlProjectionExpression(select, new SqlFunctionCallExpression(typeof(int), SqlFunction.RecordsAffected), aggregator, false);
		}
		
		private Expression BindStringMethod(MethodCallExpression methodCallExpression)
		{
			Expression operand0, operand1;

			switch (methodCallExpression.Method.Name)
			{
			case "Contains":
				operand0 = Visit(methodCallExpression.Object);

				if (methodCallExpression.Arguments.Count == 1)
				{
					operand1 = Visit(methodCallExpression.Arguments[0]);

					return new SqlFunctionCallExpression(methodCallExpression.Type, SqlFunction.ContainsString, operand0, operand1);
				}

				break;
			case "StartsWith":
				operand0 = Visit(methodCallExpression.Object);

				if (methodCallExpression.Arguments.Count == 1)
				{
					operand1 = Visit(methodCallExpression.Arguments[0]);

					return new SqlFunctionCallExpression(methodCallExpression.Type, SqlFunction.StartsWith, operand0, operand1);
				}

				break;
			case "EndsWith":
				operand0 = Visit(methodCallExpression.Object);

				if (methodCallExpression.Arguments.Count == 1)
				{
					operand1 = Visit(methodCallExpression.Arguments[0]);

					return new SqlFunctionCallExpression(methodCallExpression.Type, SqlFunction.EndsWith, operand0, operand1);
				}

				break;
			case "Substring":
				operand0 = Visit(methodCallExpression.Object);
				operand1 = Visit(methodCallExpression.Arguments[0]);

				if (methodCallExpression.Arguments.Count > 2)
				{
					var operand2 = Visit(methodCallExpression.Arguments[1]);

					return new SqlFunctionCallExpression(methodCallExpression.Type, SqlFunction.Substring, operand0, operand1, operand2);
				}

				return new SqlFunctionCallExpression(methodCallExpression.Type, SqlFunction.Substring, operand0, operand1);
			case "Trim":
				operand0 = Visit(methodCallExpression.Object);

				if (methodCallExpression.Arguments.Count == 1)
				{
					if (!(methodCallExpression.Arguments[0] is NewArrayExpression newArrayExpression) || newArrayExpression.Expressions.Count > 0)
					{
						throw new NotSupportedException(nameof(string.Trim));
					}
				}

				return new SqlFunctionCallExpression(methodCallExpression.Type, SqlFunction.Trim, operand0);
			case "TrimStart":
				operand0 = Visit(methodCallExpression.Object);

				if (methodCallExpression.Arguments.Count == 1)
				{
					if ((!(methodCallExpression.Arguments[0] is NewArrayExpression newArrayExpression) || newArrayExpression.Expressions.Count > 0)
						&& !(methodCallExpression.Arguments[0] is ConstantExpression) && !(methodCallExpression.Arguments[0] is SqlConstantPlaceholderExpression))
					{
						throw new NotSupportedException(nameof(string.TrimStart));
					}
				}

				return new SqlFunctionCallExpression(methodCallExpression.Type, SqlFunction.TrimLeft, operand0);
			case "TrimEnd":
				operand0 = Visit(methodCallExpression.Object);

				if (methodCallExpression.Arguments.Count == 1)
				{
					if ((!(methodCallExpression.Arguments[0] is NewArrayExpression newArrayExpression) || newArrayExpression.Expressions.Count > 0)
						&& !(methodCallExpression.Arguments[0] is ConstantExpression) && !(methodCallExpression.Arguments[0] is SqlConstantPlaceholderExpression))
					{
						throw new NotSupportedException(nameof(string.TrimEnd));
					}
				}

				return new SqlFunctionCallExpression(methodCallExpression.Type, SqlFunction.TrimRight, operand0);
			case "ToUpper":
				operand0 = Visit(methodCallExpression.Object);

				if (methodCallExpression.Arguments.Count != 0)
				{
					throw new NotSupportedException(nameof(string.ToUpper));
				}

				return new SqlFunctionCallExpression(methodCallExpression.Type, SqlFunction.Upper, operand0);
			case "ToLower":
				operand0 = Visit(methodCallExpression.Object);

				if (methodCallExpression.Arguments.Count != 0)
				{
					throw new NotSupportedException(nameof(string.ToLower));
				}

				return new SqlFunctionCallExpression(methodCallExpression.Type, SqlFunction.Lower, operand0);
			case "IsNullOrEmpty":
				operand0 = Visit(methodCallExpression.Arguments[0]);

				return Expression.Or(Expression.Equal(operand0, Expression.Constant(null)), Expression.Equal(operand0, Expression.Constant(string.Empty)));
			}

			return base.VisitMethodCall(methodCallExpression);
		}

		private MemberInitExpression RemoveNonPrimaryKeyBindings(MemberInitExpression memberInitExpression)
		{
			var typeDescriptor = this.typeDescriptorProvider.GetTypeDescriptor(memberInitExpression.Type);

			var newBindings = new List<MemberBinding>();

			foreach (var binding in memberInitExpression.Bindings)
			{
				if (binding.BindingType == MemberBindingType.Assignment)
				{
					var assignment = (MemberAssignment)binding;
					var propertyDescriptor = typeDescriptor.GetPropertyDescriptorByPropertyName(binding.Member.Name);

					if (!propertyDescriptor.IsPrimaryKey)
					{
						continue;
					}

					var assignmentExpression = assignment.Expression.StripObjectBindingCalls();

					if (assignmentExpression.NodeType == ExpressionType.MemberInit)
					{
						newBindings.Add(Expression.Bind(assignment.Member, RemoveNonPrimaryKeyBindings((MemberInitExpression)assignmentExpression)));
					}
					else
					{
						newBindings.Add(binding);
					}
				}
			}

			return Expression.MemberInit(memberInitExpression.NewExpression, newBindings);
		}

		protected virtual Expression BindOrderBy(Type resultType, Expression source, LambdaExpression selector, SortOrder sortOrder)
		{
			return BindOrderByThenBys(resultType, source, new[] { selector }, new [] { sortOrder });
		}

		protected virtual Expression BindOrderByThenBys(Type resultType, Expression source, LambdaExpression[] orderByThenBys, SortOrder[] sortOrders)
		{
			var projection = (SqlProjectionExpression)Visit(source);
			var alias = GetNextAlias();
			var projectedColumns = ProjectColumns(projection.Projector, alias, null, projection.Select.Alias);
			List<SqlOrderByExpression> orderByExpressions = null;

			for (var i = 0; i < orderByThenBys.Length; i++)
			{
				var sortOrder = sortOrders[i];
				var selector = orderByThenBys[i];

				AddExpressionByParameter(selector.Parameters[0], projection.Projector);

				var expression = Visit(selector.Body).StripObjectBindingCalls();

				if (expression.NodeType == ExpressionType.MemberInit)
				{
					var memberInitExpression = (MemberInitExpression)expression;

					expression = RemoveNonPrimaryKeyBindings(memberInitExpression);
				}

				if (orderByExpressions == null)
				{
					orderByExpressions = ProjectColumns(expression, alias, null, projection.Select.Alias).Columns.Select(column => new SqlOrderByExpression(sortOrder == SortOrder.Ascending ? OrderType.Ascending : OrderType.Descending, column.Expression)).ToList();
				}
				else
				{
					orderByExpressions.AddRange(ProjectColumns(expression, alias, null, projection.Select.Alias).Columns.Select(column => new SqlOrderByExpression(sortOrder == SortOrder.Ascending ? OrderType.Ascending : OrderType.Descending, column.Expression)));
				}
			}

			return new SqlProjectionExpression(new SqlSelectExpression(resultType, alias, projectedColumns.Columns, projection.Select, null, orderByExpressions.AsReadOnly(), projection.Select.ForUpdate), projectedColumns.Projector, null);
		}

		private Expression BindUnion(Type resultType, Expression left, Expression right, bool unionAll)
		{
			var leftProjection = VisitSequence(left);
			var rightProjection = VisitSequence(right);

			var unionAlias = GetNextAlias();
			var union = new SqlUnionExpression(resultType, unionAlias, leftProjection, rightProjection, unionAll);

			var alias = GetNextAlias();
			var projectedColumns = ProjectColumns(leftProjection.Projector, alias, null, leftProjection.Select.Alias);
			var columns = projectedColumns.Columns.Select(c => c.Expression.NodeType == (ExpressionType)SqlExpressionType.Column ? c.ReplaceExpression(((SqlColumnExpression)c.Expression).ChangeAlias(unionAlias)) : c);

			return new SqlProjectionExpression(new SqlSelectExpression(resultType, alias, columns, union, null, null, leftProjection.Select.ForUpdate || rightProjection.Select.ForUpdate), projectedColumns.Projector, null);
		}

		private Expression BindForUpdate(Expression source)
		{
			var projection = (SqlProjectionExpression)Visit(source);

			var newSelect = projection.Select.ChangeForUpdate(true);

			return projection.ChangeSelect(newSelect);
		}

		private Expression BindWhere(Type resultType, Expression source, LambdaExpression predicate, bool sourceAlreadyVisited = false)
		{
			var projection = (SqlProjectionExpression)(sourceAlreadyVisited ? source : VisitSequence(source));

			AddExpressionByParameter(predicate.Parameters[0], projection.Projector);

			var where = Visit(predicate.Body);

			var alias = GetNextAlias();

			var pc = ProjectColumns(projection.Projector, alias, null, GetExistingAlias(projection.Select));

			var retval = new SqlProjectionExpression(new SqlSelectExpression(resultType, alias, pc.Columns, projection.Select, where, null, null, false, null, null, projection.Select.ForUpdate, false), pc.Projector, null);

			return retval;
		}

		private Expression BindDistinct(Type resultType, Expression source)
		{
			var projection = VisitSequence(source);
			var select = projection.Select;
			var alias = GetNextAlias();

			var projectedColumns = ProjectColumns(projection.Projector, alias, null, projection.Select.Alias);

			return new SqlProjectionExpression(new SqlSelectExpression(resultType, alias, projectedColumns.Columns, projection.Select, null, null, null, true, null, null, select.ForUpdate), projectedColumns.Projector, null);
		}

		private Expression currentGroupElement;

		private static SqlAggregateType GetAggregateType(string methodName)
		{
			switch (methodName)
			{
			case "Count":
				return SqlAggregateType.Count;
			case "LongCount":
				return SqlAggregateType.LongCount;
			case "Min":
				return SqlAggregateType.Min;
			case "Max":
				return SqlAggregateType.Max;
			case "Sum":
				return SqlAggregateType.Sum;
			case "Average":
				return SqlAggregateType.Average;
			default:
				throw new Exception($"Unknown aggregate type: {methodName}");
			}
		}

		private static bool HasPredicateArg(SqlAggregateType aggregateType)
		{
			return aggregateType == SqlAggregateType.Count || aggregateType == SqlAggregateType.LongCount;
		}

		private SqlProjectionExpression VisitSequence(Expression source)
		{
			return ConvertToSequence(Visit(source));
		}

		private SqlProjectionExpression ConvertToSequence(Expression expression)
		{
			if (expression is ParameterExpression)
			{
				this.expressionsByParameter.TryGetValue((ParameterExpression)expression, out expression);
			}

			switch (expression.NodeType)
			{
			case (ExpressionType)SqlExpressionType.Projection:
				return (SqlProjectionExpression)expression;
			case ExpressionType.New:
				var newExpression = (NewExpression)expression;

				if (expression.Type.GetGenericTypeDefinitionOrNull() == typeof(Grouping<,>))
				{
					return (SqlProjectionExpression)newExpression.Arguments[1];
				}

				goto default;
			case ExpressionType.MemberAccess:
				var memberAccessExpression = (MemberExpression)expression;

				if (expression.Type.GetGenericTypeDefinitionOrNull() == TypeHelper.RelatedDataAccessObjectsType)
				{
					var typeDescriptor = this.typeDescriptorProvider.GetTypeDescriptor(expression.Type.GetGenericArguments()[0]);
					var parentTypeDescriptor = this.typeDescriptorProvider.GetTypeDescriptor(memberAccessExpression.Expression.Type);
					var source = Expression.Constant(null, this.DataAccessModel.RuntimeDataAccessModelInfo.GetDataAccessObjectsType(typeDescriptor.Type));
					var concreteType = this.DataAccessModel.GetConcreteTypeFromDefinitionType(typeDescriptor.Type);
					var parameter = Expression.Parameter(typeDescriptor.Type, "relatedObject");
					var relatedProperty = typeDescriptor.GetRelatedProperty(parentTypeDescriptor.Type);

					var relatedPropertyName = relatedProperty.PersistedName;

					var body = Expression.Equal
					(
						Expression.Property(parameter, relatedProperty),
						memberAccessExpression.Expression
					);

					var condition = Expression.Lambda(body, parameter);

					return (SqlProjectionExpression)BindWhere(expression.Type.GetGenericArguments()[0], source, condition);
				}
				else if (expression.Type.GetGenericTypeDefinitionOrNull() == TypeHelper.IQueryableType)
				{
					if (memberAccessExpression.Expression.NodeType == ExpressionType.Constant || memberAccessExpression.Expression.NodeType == (ExpressionType)SqlExpressionType.ConstantPlaceholder)
					{
						return null;
					}

					var elementType = TypeHelper.GetElementType(expression.Type);

					return GetTableProjection(elementType);
				}
				goto default;
			default:
				throw new Exception($"The expression of type '{expression.Type}' is not a sequence");
			}
		}

		private static readonly HashSet<string> ValuesRequiredAggregateNames = new HashSet<string>(new[] { "Max", "Min", "Average", "Sum" });

		private Expression BindAggregate(Expression source, MethodInfo method, LambdaExpression argument, bool isRoot)
		{
			var isDistinct = false;
			var returnType = method.ReturnType;
			Type nullableReturnType = null;
			var aggregateType = GetAggregateType(method.Name);
			var hasPredicateArg = HasPredicateArg(aggregateType);
			
			if (argument == null && !hasPredicateArg)
			{
				isDistinct = source.TryStripDistinctCall(out source);
			}

			var unwrap = false;
			var defaultIfEmpty = false;
			Expression defaultIfEmptyValue = null;

			if (isRoot)
			{
				defaultIfEmpty = source.TryStripDefaultIfEmptyCall(out source, out defaultIfEmptyValue);

				if (ValuesRequiredAggregateNames.Contains(method.Name))
				{
					if (!returnType.IsValueType || returnType.IsNullableType())
					{
						nullableReturnType = returnType;
					}
					else
					{
						unwrap = true;
						nullableReturnType = typeof(Nullable<>).MakeGenericType(returnType);
					}
				}
			}

			var projection = VisitSequence(source);

			Expression argumentExpression = null;

			if (argument != null)
			{
				AddExpressionByParameter(argument.Parameters[0], projection.Projector);

				argumentExpression = Visit(argument.Body);
			}
			else if (!hasPredicateArg)
			{
				argumentExpression = projection.Projector;
			}

			var alias = GetNextAlias();

			var aggregateExpression = new SqlAggregateExpression(nullableReturnType ?? returnType, aggregateType, argumentExpression, isDistinct);
			var selectType = typeof(IEnumerable<>).MakeGenericType(nullableReturnType ?? returnType);

			var aggregateName = isRoot ? string.Empty : GetNextAggr();

			var select = new SqlSelectExpression
			(
				selectType,
				alias,
				new[] { new SqlColumnDeclaration(aggregateName, aggregateExpression) },
				projection.Select,
				null,
				null,
				projection.Select.ForUpdate
			);

			if (isRoot)
			{
				LambdaExpression aggregator;
				var projectorReturnType = nullableReturnType ?? returnType;
				var parameterExpression = Expression.Parameter(selectType);

				if (method.Name == "Count" || method.Name == "LongCount")
				{
					if (defaultIfEmpty)
					{
						aggregator = Expression.Lambda
						(
							Expression.Call(MethodInfoFastRef.EnumerableSingleOrSpecifiedValueIfFirstIsDefaultValueMethod.MakeGenericMethod(returnType), parameterExpression, Expression.Constant(1)),
							parameterExpression
						);
					}
					else
					{
						aggregator = Expression.Lambda
						(
							Expression.Call
							(
								MethodInfoFastRef.EnumerableSingleMethod.MakeGenericMethod(returnType),
								parameterExpression
							),
							parameterExpression
						);
					}
				}
				else
				{
					if (defaultIfEmpty)
					{
						if (defaultIfEmptyValue != null)
						{
							if (defaultIfEmptyValue.Type != projectorReturnType)
							{
								defaultIfEmptyValue = Expression.Convert(defaultIfEmptyValue, projectorReturnType);
							}
						}
						else
						{
							defaultIfEmptyValue = Expression.Constant(returnType.GetDefaultValue(), projectorReturnType);
						}

						MethodInfo methodToCall;

						if ((method.Name == "Sum" || method.Name == "Average") && projectorReturnType.IsNullableType())
						{
							methodToCall = MethodInfoFastRef.EnumerableDefaultIfEmptyCoalesceSpecifiedValueMethod.MakeGenericMethod(projectorReturnType.GetUnwrappedNullableType());
						}
						else
						{
							methodToCall = MethodInfoFastRef.EnumerableDefaultIfEmptyWithValueMethod.MakeGenericMethod(projectorReturnType);
						}

						aggregator = Expression.Lambda
						(
							Expression.Convert
							(
								Expression.Call
								(
									MethodInfoFastRef.EnumerableSingleMethod.MakeGenericMethod(projectorReturnType),
									Expression.Call
									(
										methodToCall,
										Expression.Call
										(
											MethodInfoFastRef.EnumerableEmptyIfFirstIsNullMethod.MakeGenericMethod(projectorReturnType),
											parameterExpression
										),
										defaultIfEmptyValue
									)
								),
								returnType
							),
							parameterExpression
						);
					}
					else
					{
						Expression callExpression;

						if (unwrap)
						{
							if (method.Name == "Sum" || method.Name == "Average")
							{
								callExpression = Expression.Call
								(
									MethodInfoFastRef.EnumerableSingleMethod.MakeGenericMethod(nullableReturnType),
									parameterExpression
								);

								callExpression = Expression.Coalesce(callExpression, Expression.Constant(returnType.GetUnwrappedNullableType().GetDefaultValue(), returnType));
							}
							else
							{
								callExpression = Expression.Call
								(
									MethodInfoFastRef.EnumerableUtilsSingleOrExceptionIfFirstIsNullMethod.MakeGenericMethod(returnType),
									parameterExpression
								);
							}
						}
						else
						{
							callExpression = Expression.Call
							(
								MethodInfoFastRef.EnumerableSingleMethod.MakeGenericMethod(returnType),
								parameterExpression
							);

							if (method.Name == "Sum" || method.Name == "Average")
							{
								callExpression = Expression.Coalesce(callExpression, Expression.Constant(returnType.GetUnwrappedNullableType().GetDefaultValue(), returnType));
							}
						}

						aggregator = Expression.Lambda
						(
							callExpression,
							parameterExpression
						);
					}
				}

				return new SqlProjectionExpression(select, new SqlColumnExpression(projectorReturnType, alias, aggregateName), aggregator, false, projection.DefaultValue);
			}

			var subquery = new SqlSubqueryExpression(returnType, select);
			
			if (this.groupByMap.TryGetValue(projection, out var info))
			{
				if (argument != null)
				{
					AddExpressionByParameter(argument.Parameters[0], info.Element);

					argumentExpression = Visit(argument.Body);
				}
				else if (!hasPredicateArg)
				{
					argumentExpression = info.Element;
				}

				aggregateExpression = new SqlAggregateExpression(returnType, aggregateType, argumentExpression, isDistinct);

				if (projection == this.currentGroupElement)
				{
					return aggregateExpression;
				}

				return new SqlAggregateSubqueryExpression(info.Alias, aggregateExpression, subquery);
			}

			return subquery;
		}

		private string GetNextAggr()
		{
			return "__TAGGR" + this.aggregateCount++;
		}

		private Expression BindSelect(Type resultType, Expression source, LambdaExpression selector, bool forUpdate)
		{
			var projection = (SqlProjectionExpression)Visit(source);

			AddExpressionByParameter(selector.Parameters[0], projection.Projector);

			this.currentOrderBys.Clear();

			var expression = Visit(selector.Body);
			var alias = GetNextAlias();
			var pc = ProjectColumns(expression, alias, null, projection.Select.Alias);
			var orderBys = this.currentOrderBys.Count == 0 ? null : this.currentOrderBys.Select(c => new SqlOrderByExpression(OrderType.Ascending, c));
			
			return new SqlProjectionExpression(new SqlSelectExpression(resultType, alias, pc.Columns, projection.Select, null, orderBys, forUpdate || projection.Select.ForUpdate), pc.Projector, null);
		}

		private Expression BindDelete(Expression source)
		{
			var projection = (SqlProjectionExpression)Visit(source);

			var alias = GetNextAlias();
			var deleteExpression = new SqlDeleteExpression(projection, null);
			var select = new SqlSelectExpression(typeof(int), alias, new [] { new SqlColumnDeclaration("__SHAOLINQ__DELETE", Expression.Constant(null), true) }, deleteExpression, null, null, projection.Select.ForUpdate);

			var parameterExpression = Expression.Parameter(typeof(IEnumerable<int>));
			
			var aggregator = Expression.Lambda
			(
				Expression.Call(MethodInfoFastRef.EnumerableExtensionsAlwaysReadFirstMethod.MakeGenericMethod(typeof(int)), parameterExpression),
				parameterExpression
			);

			return new SqlProjectionExpression(select, new SqlFunctionCallExpression(typeof(int), SqlFunction.RecordsAffected), aggregator, false);
		}

		private static string GetExistingAlias(Expression source)
		{
			switch ((SqlExpressionType)source.NodeType)
			{
			case SqlExpressionType.Select:
				return ((SqlSelectExpression)source).Alias;
			case SqlExpressionType.Table:
				return ((SqlTableExpression)source).Alias;
			default:
				throw new InvalidOperationException($"Invalid source node type: {source.NodeType}");
			}
		}

		private class MemberInitEqualityComparer
			: IEqualityComparer<MemberInitExpression>
		{
			public static readonly MemberInitEqualityComparer Default = new MemberInitEqualityComparer();

			public bool Equals(MemberInitExpression x, MemberInitExpression y)
			{
				return x.Type == y.Type && x.Bindings.Count == y.Bindings.Count;
			}

			public int GetHashCode(MemberInitExpression obj)
			{
				return obj.Type.GetHashCode() ^ obj.Bindings.Count;
			}
		}

		private static readonly PropertyWiseEqualityComparer<Pair<List<MemberBinding>, PropertyInfo>> PropertyWiseComparerer = new PropertyWiseEqualityComparer<Pair<List<MemberBinding>, PropertyInfo>>(type => typeof(ObjectReferenceIdentityEqualityComparer<>).MakeGenericType(type).GetProperty("DefaultConfiguration", BindingFlags.Static | BindingFlags.Public).GetGetMethod().Invoke(null, null));

		private SqlProjectionExpression GetTableProjection(Type type)
		{
			Type elementType;
			TypeDescriptor typeDescriptor;

			if (type.IsDataAccessObjectType())
			{
				typeDescriptor = this.typeDescriptorProvider.GetTypeDescriptor(type);

				elementType = typeDescriptor.Type;
			}
			else
			{
				typeDescriptor = this.typeDescriptorProvider.GetTypeDescriptor(TypeHelper.GetElementType(type));

				elementType = typeDescriptor.Type;
			}

			var tableAlias = GetNextAlias();
			var selectAlias = GetNextAlias();

			var rootBindings = new List<MemberBinding>();
			var tableColumns = new List<SqlColumnDeclaration>();

			var columnInfos = GetColumnInfos
			(
				this.typeDescriptorProvider,
				typeDescriptor.PersistedProperties,
				(c, d) => d == 0 || c.IsPrimaryKey,
				(c, d) => d == 0 || c.IsPrimaryKey
			);

			var groupedColumnInfos = columnInfos
				.GroupBy(c => c.VisitedProperties, ArrayEqualityComparer<PropertyDescriptor>.Default)
				.ToList();

			var bindingsForKey = groupedColumnInfos
				.ToDictionary(c => c.Key, c => c.Key.Length == 0 ? rootBindings : new List<MemberBinding>(), ArrayEqualityComparer<PropertyDescriptor>.Default);

			bindingsForKey[new PropertyDescriptor[0]] = rootBindings;

			var readonlyBindingsForKey = bindingsForKey
				.Select(c => new { c.Key, Value = c.Value.ToReadOnlyCollection() })
				.ToDictionary(c => c.Key, c => c.Value, ArrayEqualityComparer<PropertyDescriptor>.Default);

			var parentBindingsForKey = bindingsForKey
				.Where(c => c.Key.Length > 0)
				.ToDictionary(c => c.Key, c => bindingsForKey[c.Key.Take(c.Key.Length - 1).ToArray()], ArrayEqualityComparer<PropertyDescriptor>.Default);

			var rootPrimaryKeyProperties = new HashSet<string>(typeDescriptor.PrimaryKeyProperties.Select(c => c.PropertyName));

			var propertyAdded = new HashSet<Pair<List<MemberBinding>, PropertyInfo>>(PropertyWiseComparerer);

			foreach (var value in columnInfos)
			{
				var currentBindings = bindingsForKey[value.VisitedProperties];

				var columnExpression = new SqlColumnExpression(value.DefinitionProperty.PropertyType, selectAlias, value.ColumnName);

				currentBindings.Add(Expression.Bind(value.DefinitionProperty, columnExpression));

				tableColumns.Add(new SqlColumnDeclaration(value.ColumnName, new SqlColumnExpression(value.DefinitionProperty.PropertyType, tableAlias, value.ColumnName)));

				if (value.VisitedProperties.Length > 0)
				{
					var property = value.VisitedProperties.Last();
					var parentBindings = parentBindingsForKey[value.VisitedProperties];

					if (parentBindings.All(c => c.Member != property.PropertyInfo))
					{
						var objectReferenceType = property.PropertyType;
						var objectReference = new SqlObjectReferenceExpression(objectReferenceType, readonlyBindingsForKey[value.VisitedProperties]);

						parentBindings.Add(Expression.Bind(property, objectReference));

						propertyAdded.Add(new Pair<List<MemberBinding>, PropertyInfo>(parentBindings, property));
					}
				}
			}

			var rootObjectReference = new SqlObjectReferenceExpression(typeDescriptor.Type, rootBindings.Where(c => rootPrimaryKeyProperties.Contains(c.Member.Name)));

			if (rootObjectReference.Bindings.Count == 0)
			{
				throw new InvalidOperationException($"Missing ObjectReference bindings: {type.Name}");
			}

			var projectorExpression = Expression.MemberInit(Expression.New(elementType), rootBindings);

			var resultType = typeof(IEnumerable<>).MakeGenericType(elementType);
			var projection = new SqlProjectionExpression(new SqlSelectExpression(resultType, selectAlias, tableColumns, new SqlTableExpression(resultType, tableAlias, typeDescriptor.PersistedName), null, null, false), projectorExpression, null);

			return projection;
		}

		protected override Expression VisitConstant(ConstantExpression constantExpression)
		{
			if (constantExpression.Value is IQueryable queryable && queryable.Expression != constantExpression)
			{
				return Visit(queryable.Expression);
			}

			var type = constantExpression.Type;

			if (constantExpression.Value != null)
			{
				type = constantExpression.Value.GetType();
			}

			if (typeof(DataAccessObjectsQueryable<>).IsAssignableFromIgnoreGenericParameters(type)
				&& (!(constantExpression.Value is IHasDataAccessModel) || ((IHasDataAccessModel)constantExpression.Value).DataAccessModel == this.DataAccessModel))
			{
				var retval = GetTableProjection(type);

				var hasCondition = constantExpression.Value as IHasCondition;

				if (hasCondition?.Condition != null)
				{
					return BindWhere(retval.Type, retval, hasCondition.Condition, true);
				}

				return retval;
			}
			else if (type.IsDataAccessObjectType())
			{
				return CreateObjectReference(constantExpression);
			}

			return constantExpression;
		}

		protected override Expression VisitParameter(ParameterExpression parameterExpression)
		{
			if (this.expressionsByParameter.TryGetValue(parameterExpression, out var retval))
			{
				return retval;
			}

			return parameterExpression;
		}

		protected SqlObjectReferenceExpression CreateObjectReference(Expression expression)
		{
			var typeDescriptor = this.typeDescriptorProvider.GetTypeDescriptor(this.DataAccessModel.GetDefinitionTypeFromConcreteType(expression.Type));

			var columnInfos = GetColumnInfos
			(
				this.typeDescriptorProvider,
				typeDescriptor.PersistedProperties,
				(c, d) => c.IsPrimaryKey,
				(c, d) => c.IsPrimaryKey
			);

			var groupedColumnInfos = columnInfos
				.GroupBy(c => c.VisitedProperties, ArrayEqualityComparer<PropertyDescriptor>.Default)
				.ToList();

			var expressionForKey = new Dictionary<PropertyDescriptor[], Expression>(ArrayEqualityComparer<PropertyDescriptor>.Default);

			var bindings = new List<MemberBinding>();

			expressionForKey[new PropertyDescriptor[0]] = expression;

			var constantValue = (expression as ConstantExpression).Value;

			foreach (var groupedColumnInfo in groupedColumnInfos)
			{
				Expression parentExpression;
				var parentKey = groupedColumnInfo.Key.Length == 0 ? null : groupedColumnInfo.Key.Take(groupedColumnInfo.Key.Length - 1).ToArray();

				if (parentKey == null)
				{
					parentExpression = expression;
				}
				else
				{
					parentExpression = Expression.Property(parentKey.Length == 0 ? expression : expressionForKey[parentKey], groupedColumnInfo.Key[groupedColumnInfo.Key.Length - 1].PropertyInfo);
				}

				expressionForKey[groupedColumnInfo.Key] = parentExpression;

				foreach (var value in groupedColumnInfo)
				{
					if (constantValue != null && parentExpression.Type.IsDataAccessObjectType())
					{
						var parentValue = ExpressionInterpreter.Interpret(parentExpression) as DataAccessObject;

						if (parentValue.GetAdvanced().DeflatedPredicate != null)
						{
							var subqueryExpression = (Expression)MethodInfoFastRef.DataAccessObjectHelpersInternalGetPropertyValueExpressionFromPredicatedDeflatedObject
								.MakeGenericMethod(this.DataAccessModel.GetDefinitionTypeFromConcreteType(parentExpression.Type), value.DefinitionProperty.PropertyType)
								.Invoke(null, new object[] { parentValue, value.DefinitionProperty.PropertyName });

							var subqueryExpression2 = Evaluator.PartialEval(subqueryExpression, ref placeholderCount);

							bindings.Add(Expression.Bind(value.DefinitionProperty, Visit(subqueryExpression2)));

							continue;
						}
					}

					var propertyAccess = Expression.Property(parentExpression, value.DefinitionProperty.PropertyInfo);

					bindings.Add(Expression.Bind(value.DefinitionProperty, propertyAccess));
				}
			}

			if (bindings.Count == 0)
			{
				throw new Exception($"Missing bindings for: {expression}");
			}

			return new SqlObjectReferenceExpression(this.DataAccessModel.GetDefinitionTypeFromConcreteType(expression.Type), bindings);
		}

		protected override Expression VisitMemberAccess(MemberExpression memberExpression)
		{
			var source = Visit(memberExpression.Expression).StripObjectBindingCalls();

			if (source == null)
			{
				return MakeMemberAccess(null, memberExpression.Member);
			}

			if ((SqlExpressionType)source.NodeType == SqlExpressionType.Projection)
			{
				var alias = GetNextAlias();
				var projection = (SqlProjectionExpression)source;
				var from = ((SqlProjectionExpression)source).Select;

				var memberInit = projection.Projector as MemberInitExpression;

				var assign = memberInit.Bindings.OfType<MemberAssignment>().First(c => MembersMatch(c.Member, memberExpression.Member));

				if (assign == null)
				{
					throw new NotSupportedException(assign.Member.ToString());
				}

				var columns = new List<SqlColumnDeclaration>()
				{
					new SqlColumnDeclaration(memberExpression.Member.Name, assign.Expression)
				};

				return new SqlSelectExpression(memberExpression.Type, alias, columns, from, null, null);
			}

			switch (source.NodeType)
			{
			case ExpressionType.MemberInit:
				{
					var min = (MemberInitExpression)source;

					var type = memberExpression.Type.GetGenericTypeDefinitionOrNull();

					if (type == typeof(RelatedDataAccessObjects<>))
					{
						var inner = GetTableProjection(memberExpression.Type.GetSequenceElementType());
						var relationship = this.typeDescriptorProvider
							.GetTypeDescriptor(source.Type)
							.GetRelationshipInfos()
							.Where(c => c.RelationshipType == RelationshipType.ParentOfOneToMany)
							.Single(c => c.ReferencingProperty == memberExpression.Member);

						var param = Expression.Parameter(memberExpression.Type.GetSequenceElementType());
						var where = Expression.Lambda(Expression.Equal(Expression.Property(param, relationship.TargetProperty), source), param);

						return BindWhere(memberExpression.Type, inner, where, true);
					}

					foreach (var binding in min.Bindings)
					{
						if (binding is MemberAssignment assign && MembersMatch(assign.Member, memberExpression.Member))
						{
							return assign.Expression;
						}
					}

					break;
				}
			case ExpressionType.New:
				{
					// Source is a anonymous type from a join
					var newExpression = (NewExpression)source;

					if (newExpression.Members != null)
					{
						for (int i = 0, n = newExpression.Members.Count; i < n; i++)
						{
							if (MembersMatch(newExpression.Members[i], memberExpression.Member))
							{
								return newExpression.Arguments[i];
							}
						}
					}
					else if (newExpression.Type.IsGenericType && newExpression.Type.Namespace == "System" && newExpression.Type.Assembly == typeof(Tuple<>).Assembly && newExpression.Type.Name.StartsWith("Tuple`"))
					{
						var i = Convert.ToInt32(memberExpression.Member.Name.Substring(4)) - 1;

						return newExpression.Arguments[i];
					}
					break;
				}
			case ExpressionType.Constant:
			case ((ExpressionType)SqlExpressionType.ConstantPlaceholder):

				if (memberExpression.Type.IsDataAccessObjectType())
				{
					return CreateObjectReference(memberExpression);
				}
				else if (typeof(DataAccessObjectsQueryable<>).IsAssignableFromIgnoreGenericParameters(memberExpression.Type))
				{
					return GetTableProjection(memberExpression.Type);
				}
				else
				{
					return memberExpression;
				}
			}

			if (source == memberExpression.Expression)
			{
				return memberExpression;
			}

			return MakeMemberAccess(source, memberExpression.Member);
		}

		private static bool MemberInfosMostlyMatch(MemberInfo a, MemberInfo b)
		{
			if (a == b)
			{
				return true;
			}

			if (a.GetType() == b.GetType())
			{
				if (a.Name == b.Name && ((a.DeclaringType == b.DeclaringType)
						|| (a.ReflectedType ?? a.DeclaringType).IsAssignableFrom(b.ReflectedType ?? b.DeclaringType)
						|| (b.ReflectedType ?? b.DeclaringType).IsAssignableFrom(a.ReflectedType ?? a.DeclaringType)))
				{
					return true;
				}
			}

			return false;
		}

		private static bool MembersMatch(MemberInfo a, MemberInfo b)
		{
			if (a == b)
			{
				return true;
			}

			if (MemberInfosMostlyMatch(a, b))
			{
				return true;
			}

			if (a is MethodInfo && b is PropertyInfo)
			{
				return MemberInfosMostlyMatch(a, ((PropertyInfo)b).GetGetMethod());
			}
			else if (a is PropertyInfo && b is MethodInfo)
			{
				return MemberInfosMostlyMatch(((PropertyInfo)a).GetGetMethod(), b);
			}

			return false;
		}

		protected override Expression VisitUnary(UnaryExpression unaryExpression)
		{
			if (unaryExpression.NodeType == ExpressionType.Not)
			{
				if (unaryExpression.Operand.NodeType == ExpressionType.Call)
				{
					if (((MethodCallExpression)unaryExpression.Operand).Method == typeof(ShaolinqStringExtensions).GetMethod("IsLike", BindingFlags.Static | BindingFlags.Public))
					{
						var methodCallExpression = (MethodCallExpression)unaryExpression.Operand;

						var operand1 = Visit(methodCallExpression.Arguments[0]);
						var operand2 = Visit(methodCallExpression.Arguments[1]);

						return new SqlFunctionCallExpression(typeof(bool), SqlFunction.NotLike, operand1, operand2);
					}
				}
			}
			
			return base.VisitUnary(unaryExpression);
		}

		private Expression MakeMemberAccess(Expression source, MemberInfo memberInfo)
		{
			var fieldInfo = memberInfo as FieldInfo;

			if (typeof(ICollection<>).IsAssignableFromIgnoreGenericParameters(memberInfo.DeclaringType))
			{
				if (memberInfo.Name == "Count")
				{
					return new SqlFunctionCallExpression(memberInfo.GetMemberReturnType(), SqlFunction.CollectionCount, source);
				}
			}
			else if (memberInfo.DeclaringType == typeof(string))
			{
				if (memberInfo.Name == "Length")
				{
					return new SqlFunctionCallExpression(memberInfo.GetMemberReturnType(), SqlFunction.StringLength, source);
				}
			}
			else if (memberInfo.DeclaringType == typeof(ServerDateTime))
			{
				switch (memberInfo.Name)
				{
					case "Now":
						return new SqlFunctionCallExpression(memberInfo.GetMemberReturnType(), SqlFunction.ServerNow);
					case "UtcNow":
						return new SqlFunctionCallExpression(memberInfo.GetMemberReturnType(), SqlFunction.ServerUtcNow);
				}
			}
			else if (typeof(IGrouping<,>).IsAssignableFromIgnoreGenericParameters(memberInfo.DeclaringType) && source is NewExpression && memberInfo.Name == "Key")
			{
				var newExpression = source as NewExpression;

				var arg = newExpression.Arguments[0];

				return arg;
			}
			else if (source != null && source.NodeType == (ExpressionType)SqlExpressionType.ObjectReference)
			{
				var objectOperandExpression = (SqlObjectReferenceExpression)source;
				var binding = objectOperandExpression.Bindings.OfType<MemberAssignment>().FirstOrDefault(c => c.Member.Name == memberInfo.Name);

				if (binding != null)
				{
					return binding.Expression;
				}
			}

			var type = memberInfo.DeclaringType;
			var typeIsNullable = Nullable.GetUnderlyingType(type) != null;
			var castMethod = typeIsNullable ? null : type.GetMethods(BindingFlags.Static | BindingFlags.Public)
				.Where(c => c.IsSpecialName)
				.Where(c => c.ReturnType == typeof(DateTime))
				.Where(c => c.Name.StartsWith("op_Implicit") || c.Name.StartsWith("op_Explicit"))
				.SingleOrDefault(c => c.GetParameters().First()?.ParameterType == type);

			var typeConvertableToDateTimeWithTypeConverterWithCast = castMethod != null;
			var typeConverter = System.ComponentModel.TypeDescriptor.GetConverter(type);
			var typeConvertableToDateTimeWithTypeConverter = !typeIsNullable
				&& typeConverter.CanConvertTo(typeof(DateTime));

			if (memberInfo.DeclaringType == typeof(DateTime) || typeConvertableToDateTimeWithTypeConverterWithCast || typeConvertableToDateTimeWithTypeConverter)
			{
				switch (memberInfo.Name)
				{
					case "Week":
						return new SqlFunctionCallExpression(memberInfo.GetMemberReturnType(), SqlFunction.Week, source);
					case "Month":
						return new SqlFunctionCallExpression(memberInfo.GetMemberReturnType(), SqlFunction.Month, source);
					case "Year":
						return new SqlFunctionCallExpression(memberInfo.GetMemberReturnType(), SqlFunction.Year, source);
					case "Hour":
						return new SqlFunctionCallExpression(memberInfo.GetMemberReturnType(), SqlFunction.Hour, source);
					case "Minute":
						return new SqlFunctionCallExpression(memberInfo.GetMemberReturnType(), SqlFunction.Minute, source);
					case "Second":
						return new SqlFunctionCallExpression(memberInfo.GetMemberReturnType(), SqlFunction.Second, source);
					case "DayOfWeek":
						return new SqlFunctionCallExpression(memberInfo.GetMemberReturnType(), SqlFunction.DayOfWeek, source);
					case "Day":
						return new SqlFunctionCallExpression(memberInfo.GetMemberReturnType(), SqlFunction.DayOfMonth, source);
					case "DayOfYear":
						return new SqlFunctionCallExpression(memberInfo.GetMemberReturnType(), SqlFunction.DayOfYear, source);
					case "Date":
						return new SqlFunctionCallExpression(memberInfo.GetMemberReturnType(), SqlFunction.Date, source);
					default:
						throw new NotSupportedException("Member access on DateTime: " + memberInfo);
				}
			}

			if (fieldInfo != null)
			{
				return Expression.Field(source, fieldInfo);
			}

			var propertyInfo = memberInfo as PropertyInfo;

			if (propertyInfo != null)
			{
				// TODO: Throw an unsupported exception if not binding for SQL ToString implementation

				return Expression.Property(source, propertyInfo);
			}

			throw new NotSupportedException("MemberInfo: " + memberInfo);
		}

		internal static string GetKey(Dictionary<string, Expression> dictionary, Expression expression)
		{
			foreach (var keyValue in dictionary)
			{
				if (keyValue.Value == expression)
				{
					return keyValue.Key;
				}
			}

			throw new InvalidOperationException();
		}

		private readonly List<Expression> currentOrderBys = new List<Expression>();

		private void AddCurrentOrderBy(Expression orderByOperand)
		{
			switch (orderByOperand)
			{
			case MemberInitExpression memberInitExpression:
				var typeDescriptor = this.typeDescriptorProvider.GetTypeDescriptor(memberInitExpression.Type);

				foreach (var property in typeDescriptor.PrimaryKeyProperties)
				{
					var expression = memberInitExpression.Bindings.OfType<MemberAssignment>().First(c => c.Member.Name == property.PropertyName).Expression;

					AddCurrentOrderBy(expression);
				}

				break;
			case SqlObjectReferenceExpression objectReferenceExpression:
				foreach (var expression in objectReferenceExpression.Bindings.OfType<MemberAssignment>().Select(c => c.Expression))
				{
					AddCurrentOrderBy(expression);
				}

				break;
			case null:
				break;
			default:
				this.currentOrderBys.Add(orderByOperand);
				break;
			}
		}

		private Expression ProcessJoins(Expression expression, List<IncludedPropertyInfo> includedPropertyInfos, int index, bool useFullPath)
		{
			expression = PrivateVisit(expression);
			var visited = new HashSet<string>();
			
			var addedRoot = false;

			// Process joins for DAO properties or RelatedDAOs

			foreach (var includedPropertyInfo in includedPropertyInfos
				.GroupBy(c => useFullPath ? c.FullAccessPropertyPath.Length : c.IncludedPropertyPath.Length)
				.OrderBy(c => c.Key)
				.First())
			{
				var expressionToReplace = expression;
				var propertyPath = useFullPath ? includedPropertyInfo.FullAccessPropertyPath : includedPropertyInfo.IncludedPropertyPath;
				var propertyInfo = propertyPath[index];
				var lastIndex = propertyPath.Length - 1;
				var currentPropertyName = propertyInfo.Name;

				if (visited.Contains(currentPropertyName))
				{
					continue;
				}

				visited.Add(currentPropertyName);

				List<IncludedPropertyInfo> nextProperties;
				var unwrapped = expressionToReplace.StripObjectBindingCalls();

				if (propertyInfo.PropertyType.GetGenericTypeDefinitionOrNull() == typeof(RelatedDataAccessObjects<>))
				{
					var value = this.joinExpanderResults.GetReplacementExpression(this.selectorPredicateStack.Peek(), includedPropertyInfo.FullAccessPropertyPath);

					value = Visit(value);

					var param = Expression.Parameter(expression.Type);

					nextProperties = includedPropertyInfos
						.Where(c => (useFullPath ? c.FullAccessPropertyPath.Length : c.IncludedPropertyPath.Length) > index + 1)
						.Where(c => (useFullPath ? c.FullAccessPropertyPath : c.IncludedPropertyPath).Take(index).SequenceEqual(propertyPath.Take(index)))
						.ToList();

					// Add OrderBy for sub-collections

					if (nextProperties.Count == 0)
					{
						if (!addedRoot)
						{
							addedRoot = true;
							AddCurrentOrderBy(expression.StripAddToCollectionCalls() as MemberInitExpression);
						}

						AddCurrentOrderBy(value);
					}

					// AddToCollection call

					expression = Expression.Call
					(
						MethodInfoFastRef.DataAccessObjectExtensionsAddToCollectionMethod.MakeGenericMethod(expression.Type, value.Type),
						expression,
						Expression.Lambda(Expression.Property(param, propertyInfo.Name), param),
						nextProperties.Count > 0 ? ProcessJoins(value, nextProperties, index + 1, useFullPath) : value,
						Expression.Call(MethodInfoFastRef.TransactionContextGetCurrentContextVersion, Expression.Constant(this.DataAccessModel))
					);
				}
				else
				{
					var memberInitExpression = unwrapped as MemberInitExpression;
					var newExpression = memberInitExpression == null ? unwrapped as NewExpression : null;

					if (memberInitExpression != null)
					{
						expressionToReplace = ((MemberAssignment)memberInitExpression.Bindings.First(c => string.Compare(c.Member.Name, currentPropertyName, StringComparison.InvariantCultureIgnoreCase) == 0))?.Expression;
					}
					else if (newExpression != null)
					{
						var x = newExpression.Constructor.GetParameters().IndexOfAny(c => string.Compare(c.Name, currentPropertyName, StringComparison.InvariantCultureIgnoreCase) == 0);

						expressionToReplace = newExpression.Arguments[x];
					}
					else
					{
						throw new InvalidOperationException();
					}

					var replacement = expressionToReplace;

					if (index == lastIndex && replacement.NodeType == (ExpressionType)SqlExpressionType.ObjectReference)
					{
						var originalReplacementExpression = this.joinExpanderResults.GetReplacementExpression(this.selectorPredicateStack.Peek(), includedPropertyInfo.FullAccessPropertyPath);

						replacement = Visit(originalReplacementExpression);
					}

					nextProperties = includedPropertyInfos
						.Where(c => (useFullPath ? c.FullAccessPropertyPath.Length : c.IncludedPropertyPath.Length) > index + 1)
						.Where(c => (useFullPath ? c.FullAccessPropertyPath : c.IncludedPropertyPath).Take(index).SequenceEqual(propertyPath.Take(index)))
						.ToList();

					if (nextProperties.Count > 0)
					{
						replacement = ProcessJoins(replacement, nextProperties, index + 1, useFullPath);
					}

					if (!ReferenceEquals(replacement, expressionToReplace))
					{
						expression = SqlExpressionReplacer.Replace(expression, expressionToReplace, replacement);
					}
				}
			}

			return expression;
		}

		protected override Expression Visit(Expression expression)
		{
			if (expression == null)
			{
				return null;
			}
			
			if (this.joinExpanderResults.IncludedPropertyInfos.TryGetValue(expression, out var includedPropertyInfos))
			{
				var useFullPath = !expression.Type.IsDataAccessObjectType();

				return ProcessJoins(expression, includedPropertyInfos, 0, useFullPath);
			}

			return PrivateVisit(expression);
		}

		private Expression PrivateVisit(Expression expression)
		{
			if (expression == null)
			{
				return null;
			}

			switch (expression.NodeType)
			{
			case (ExpressionType)SqlExpressionType.ConstantPlaceholder:
				var result = Visit(((SqlConstantPlaceholderExpression)expression).ConstantExpression);

				if (!(result is ConstantExpression))
				{
					return result;
				}

				return expression;
			case (ExpressionType)SqlExpressionType.Column:
				return expression;
			case (ExpressionType)SqlExpressionType.Projection:
				return expression;
			}

			if ((int)expression.NodeType > (int)SqlExpressionType.First)
			{
				return expression;
			}

			return base.Visit(expression);
		}
	}
}
