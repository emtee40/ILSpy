﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Management.Automation;
using System.Text;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;

namespace ICSharpCode.Decompiler.PowerShell
{
	[Cmdlet(VerbsCommon.Get, "DecompiledProject")]
	[OutputType(typeof(string))]
	public class GetDecompiledProjectCmdlet : PSCmdlet
	{
		[Parameter(Position = 0, Mandatory = true)]
		public CSharpDecompiler Decompiler { get; set; }

		[Parameter(Position = 1, Mandatory = true)]
		[Alias("PSPath", "OutputPath")]
		[ValidateNotNullOrEmpty]
		public string LiteralPath { get; set; }

		protected override void ProcessRecord()
		{
			string path = GetUnresolvedProviderPathFromPSPath(LiteralPath);
			if (!Directory.Exists(path))
			{
				WriteObject("Destination directory must exist prior to decompilation");
				return;
			}

			WholeProjectDecompiler decompiler = new WholeProjectDecompiler();
			try
			{
				decompiler.Settings = this.Decompiler.Settings;
				PEFile module = Decompiler.TypeSystem.MainModule.PEFile;
				decompiler.AssemblyResolver = new UniversalAssemblyResolver(module.FileName, false, module.Reader.DetectTargetFrameworkId());
				decompiler.DebugInfoProvider = this.Decompiler.DebugInfoProvider;
				decompiler.DecompileProject(module, path);

			}
			catch (Exception e)
			{
				WriteVerbose(e.ToString());
				WriteError(new ErrorRecord(e, ErrorIds.DecompilationFailed, ErrorCategory.OperationStopped, null));
			}
			WriteObject(decompiler);
		}
	}
}
