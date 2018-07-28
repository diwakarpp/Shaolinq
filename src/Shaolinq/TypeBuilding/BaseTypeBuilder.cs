// Copyright (c) 2007-2018 Thong Nguyen (tumtumtum@gmail.com)

using System.Reflection.Emit;

namespace Shaolinq.TypeBuilding
{
	internal abstract class BaseTypeBuilder
	{
		public ModuleBuilder ModuleBuilder { get; protected set; }
		public AssemblyBuildContext AssemblyBuildContext { get; protected set; }

		protected BaseTypeBuilder(AssemblyBuildContext assemblyBuildContext, ModuleBuilder moduleBuilder)
		{
			this.ModuleBuilder = moduleBuilder;
			this.AssemblyBuildContext = assemblyBuildContext;
		}
	}
}
