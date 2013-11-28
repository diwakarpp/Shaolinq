// Copyright (c) 2007-2013 Thong Nguyen (tumtumtum@gmail.com)

using Platform.Validation;

namespace Shaolinq.Tests.DataAccessModel.Test
{
	[DataAccessObject]
	public abstract class Fraternity
		: DataAccessObject<string>
	{
		[SizeConstraint(MaximumLength = 32)]
		public override abstract string Id
		{
			get; set; 
		}

		[RelatedDataAccessObjects]
		public abstract RelatedDataAccessObjects<Student> Students { get; }
	}
}
