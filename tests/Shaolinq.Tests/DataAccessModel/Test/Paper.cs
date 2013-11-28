// Copyright (c) 2007-2013 Thong Nguyen (tumtumtum@gmail.com)

﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Platform;
using Platform.Validation;

namespace Shaolinq.Tests.DataAccessModel.Test
{
	[DataAccessObject]
	public abstract class Paper
		: DataAccessObject<string>
	{
		[SizeConstraint(MaximumLength=32)]
		public string PaperCode
		{
			get
			{
				return this.Id;
			}
			set
			{
				this.Id = value;
			}
		}

		[RelatedDataAccessObjects]
		public abstract RelatedDataAccessObjects<Lecturer> Lecturers { get; }
	}
}
