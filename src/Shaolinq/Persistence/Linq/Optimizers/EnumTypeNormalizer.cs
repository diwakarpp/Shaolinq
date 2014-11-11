﻿using System;
using Platform;
using System.Linq.Expressions;
using Shaolinq.Persistence.Linq.Expressions;

namespace Shaolinq.Persistence.Linq.Optimizers
{
	public class EnumTypeNormalizer
		: SqlExpressionVisitor
	{
		public Type PersistedType { get; private set; }

		public EnumTypeNormalizer(Type persistedType)
		{
			this.PersistedType = persistedType;
		}

		public static Expression Normalize(Expression expression, Type persistedType)
		{
			var normalizer = new EnumTypeNormalizer(persistedType);

			return normalizer.Visit(expression);
		}

		protected override Expression VisitUnary(UnaryExpression unaryExpression)
		{
			var operand = unaryExpression.Operand;

			if (this.PersistedType == typeof(string)
				&& unaryExpression.NodeType == ExpressionType.Convert
				&& unaryExpression.Type.IsIntegerType(true))
			{
				if (operand.NodeType == (ExpressionType)SqlExpressionType.Column
					&& operand.Type.GetUnwrappedNullableType().IsEnum)
				{
					return operand;
				}
			}

			return base.VisitUnary(unaryExpression);
		}

		protected override Expression VisitBinary(BinaryExpression binaryExpression)
		{
			var left = this.Visit(binaryExpression.Left);
			var right = this.Visit(binaryExpression.Right);

			if (left.Type.GetUnwrappedNullableType().IsEnum)
			{
				if (!right.Type.GetUnwrappedNullableType().IsEnum)
				{
					var lambda = Expression.Lambda(Expression.Convert(Expression.Call(null, MethodInfoFastRef.EnumToObjectMethod, Expression.Constant(left.Type.GetUnwrappedNullableType()), Expression.Convert(right, typeof(int))), left.Type));

					right = lambda.Body;
				}
			}
			else if (right.Type.GetUnwrappedNullableType().IsEnum)
			{
				if (!left.Type.GetUnwrappedNullableType().IsEnum)
				{
					left = Expression.Convert(Expression.Call(null, MethodInfoFastRef.EnumToObjectMethod, Expression.Constant(right.Type.GetUnwrappedNullableType()), Expression.Convert(left, typeof(int))), right.Type);
				}
			}

			if (left != binaryExpression.Left || right != binaryExpression.Right)
			{
				return Expression.MakeBinary(binaryExpression.NodeType, left, right);
			}

			return binaryExpression;
		}
	}
}
