﻿using System.Diagnostics;
using System.Linq;
using ICSharpCode.Decompiler.TypeSystem;

namespace ICSharpCode.Decompiler.IL.Transforms
{
	class InlineExpressionTreeParameterDeclarationsTransform : IStatementTransform
	{
		public void Run(Block block, int pos, StatementTransformContext context)
		{
			if (block.Instructions[pos] is StLoc parameterVariableInst) {
				if (MatchParameterVariableAssignment(parameterVariableInst, out ILVariable v, out ILInstruction init)) {
					foreach (var LdParameterVariableInst in context.Function.Descendants.OfType<LdLoc>()) {
						if (LdParameterVariableInst.MatchLdLoc(v)) {
							LdParameterVariableInst.ReplaceWith(init.Clone());
						}
					}
					block.Instructions.RemoveAt(pos);
				}
			}
		}

		bool MatchParameterVariableAssignment(ILInstruction expr, out ILVariable v, out ILInstruction init)
		{
			// stloc(v, call(Expression::Parameter, call(Type::GetTypeFromHandle, ldtoken(...)), ldstr(...)))
			if (!expr.MatchStLoc(out v, out init))
				return false;
			if (/*v.HasGeneratedName || */v.Kind == VariableKind.Parameter /*|| v.IsPinned*/) // TODO
				return false;
			if (v.Type == null || v.Type.FullName != "System.Linq.Expressions.ParameterExpression")
				return false;
			if (!(init is CallInstruction initCall))
				return false;
			if (initCall.Arguments.Count != 2)
				return false;
			IMethod parameterMethod = initCall.Method;
			CallInstruction typeArg = initCall.Arguments[0] as CallInstruction;
			ILInstruction nameArg = initCall.Arguments[1];
			if (!(parameterMethod.Name == "Parameter" && parameterMethod.DeclaringType.FullName == "System.Linq.Expressions.Expression"))
				return false;
			if (typeArg == null)
				return false;
			if (typeArg.Arguments.Count != 1)
				return false;
			IMethod getTypeFromHandle = typeArg.Method;
			ILInstruction typeToken = typeArg.Arguments[0];
			if (!(getTypeFromHandle.Name == "GetTypeFromHandle" && getTypeFromHandle.DeclaringType.FullName == "System.Type"))
				return false;
			return typeToken.OpCode == OpCode.LdTypeToken && nameArg.OpCode == OpCode.LdStr;
		}
	}
}
