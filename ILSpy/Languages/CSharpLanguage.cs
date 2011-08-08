﻿// Copyright (c) 2011 AlphaSierraPapa for the SharpDevelop Team
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Resources;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xaml;
using System.Xml;

using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.Ast;
using ICSharpCode.Decompiler.Ast.Transforms;
using ICSharpCode.ILSpy.Options;
using ICSharpCode.ILSpy.XmlDoc;
using ICSharpCode.NRefactory.CSharp;
using Mono.Cecil;

namespace ICSharpCode.ILSpy
{
	/// <summary>
	/// Decompiler logic for C#.
	/// </summary>
	[Export(typeof(Language))]
	public class CSharpLanguage : Language
	{
		string name = "C#";
		bool showAllMembers = false;
		Predicate<IAstTransform> transformAbortCondition = null;

		public CSharpLanguage()
		{
		}

		#if DEBUG
		internal static IEnumerable<CSharpLanguage> GetDebugLanguages()
		{
			DecompilerContext context = new DecompilerContext(ModuleDefinition.CreateModule("dummy", ModuleKind.Dll));
			string lastTransformName = "no transforms";
			foreach (Type _transformType in TransformationPipeline.CreatePipeline(context).Select(v => v.GetType()).Distinct()) {
				Type transformType = _transformType; // copy for lambda
				yield return new CSharpLanguage {
					transformAbortCondition = v => transformType.IsInstanceOfType(v),
					name = "C# - " + lastTransformName,
					showAllMembers = true
				};
				lastTransformName = "after " + transformType.Name;
			}
			yield return new CSharpLanguage {
				name = "C# - " + lastTransformName,
				showAllMembers = true
			};
		}
		#endif

		public override string Name
		{
			get { return name; }
		}

		public override string FileExtension
		{
			get { return ".cs"; }
		}

		public override string ProjectFileExtension
		{
			get { return ".csproj"; }
		}

		public override void DecompileMethod(MethodDefinition method, ITextOutput output, DecompilationOptions options)
		{
			WriteCommentLine(output, TypeToString(method.DeclaringType, includeNamespace: true));
			AstBuilder codeDomBuilder = CreateAstBuilder(options, currentType: method.DeclaringType, isSingleMember: true);
			if (method.IsConstructor && !method.IsStatic && !method.DeclaringType.IsValueType) {
				// also fields and other ctors so that the field initializers can be shown as such
				AddFieldsAndCtors(codeDomBuilder, method.DeclaringType, method.IsStatic);
				RunTransformsAndGenerateCode(codeDomBuilder, output, options, new SelectCtorTransform(method));
			} else {
				codeDomBuilder.AddMethod(method);
				RunTransformsAndGenerateCode(codeDomBuilder, output, options);
			}
			NotifyDecompilationFinished(codeDomBuilder);
		}
		
		class SelectCtorTransform : IAstTransform
		{
			readonly MethodDefinition ctorDef;
			
			public SelectCtorTransform(MethodDefinition ctorDef)
			{
				this.ctorDef = ctorDef;
			}
			
			public void Run(AstNode compilationUnit)
			{
				ConstructorDeclaration ctorDecl = null;
				foreach (var node in compilationUnit.Children) {
					ConstructorDeclaration ctor = node as ConstructorDeclaration;
					if (ctor != null) {
						if (ctor.Annotation<MethodDefinition>() == ctorDef) {
							ctorDecl = ctor;
						} else {
							// remove other ctors
							ctor.Remove();
						}
					}
					// Remove any fields without initializers
					FieldDeclaration fd = node as FieldDeclaration;
					if (fd != null && fd.Variables.All(v => v.Initializer.IsNull))
						fd.Remove();
				}
				if (ctorDecl.Initializer.ConstructorInitializerType == ConstructorInitializerType.This) {
					// remove all fields
					foreach (var node in compilationUnit.Children)
						if (node is FieldDeclaration)
							node.Remove();
				}
			}
		}

		public override void DecompileProperty(PropertyDefinition property, ITextOutput output, DecompilationOptions options)
		{
			WriteCommentLine(output, TypeToString(property.DeclaringType, includeNamespace: true));
			AstBuilder codeDomBuilder = CreateAstBuilder(options, currentType: property.DeclaringType, isSingleMember: true);
			codeDomBuilder.AddProperty(property);
			RunTransformsAndGenerateCode(codeDomBuilder, output, options);
			NotifyDecompilationFinished(codeDomBuilder);
		}

		public override void DecompileField(FieldDefinition field, ITextOutput output, DecompilationOptions options)
		{
			WriteCommentLine(output, TypeToString(field.DeclaringType, includeNamespace: true));
			AstBuilder codeDomBuilder = CreateAstBuilder(options, currentType: field.DeclaringType, isSingleMember: true);
			if (field.IsLiteral) {
				codeDomBuilder.AddField(field);
			} else {
				// also decompile ctors so that the field initializer can be shown
				AddFieldsAndCtors(codeDomBuilder, field.DeclaringType, field.IsStatic);
			}
			RunTransformsAndGenerateCode(codeDomBuilder, output, options, new SelectFieldTransform(field));
			NotifyDecompilationFinished(codeDomBuilder);
		}
		
		/// <summary>
		/// Removes all top-level members except for the specified fields.
		/// </summary>
		sealed class SelectFieldTransform : IAstTransform
		{
			readonly FieldDefinition field;
			
			public SelectFieldTransform(FieldDefinition field)
			{
				this.field = field;
			}
			
			public void Run(AstNode compilationUnit)
			{
				foreach (var child in compilationUnit.Children) {
					if (child is AttributedNode) {
						if (child.Annotation<FieldDefinition>() != field)
							child.Remove();
					}
				}
			}
		}
		
		void AddFieldsAndCtors(AstBuilder codeDomBuilder, TypeDefinition declaringType, bool isStatic)
		{
			foreach (var field in declaringType.Fields) {
				if (field.IsStatic == isStatic)
					codeDomBuilder.AddField(field);
			}
			foreach (var ctor in declaringType.Methods) {
				if (ctor.IsConstructor && ctor.IsStatic == isStatic)
					codeDomBuilder.AddMethod(ctor);
			}
		}

		public override void DecompileEvent(EventDefinition ev, ITextOutput output, DecompilationOptions options)
		{
			WriteCommentLine(output, TypeToString(ev.DeclaringType, includeNamespace: true));
			AstBuilder codeDomBuilder = CreateAstBuilder(options, currentType: ev.DeclaringType, isSingleMember: true);
			codeDomBuilder.AddEvent(ev);
			RunTransformsAndGenerateCode(codeDomBuilder, output, options);
			NotifyDecompilationFinished(codeDomBuilder);
		}

		public override void DecompileType(TypeDefinition type, ITextOutput output, DecompilationOptions options)
		{
			AstBuilder codeDomBuilder = CreateAstBuilder(options, currentType: type);
			codeDomBuilder.AddType(type);
			RunTransformsAndGenerateCode(codeDomBuilder, output, options);
			NotifyDecompilationFinished(codeDomBuilder);
		}
		
		void RunTransformsAndGenerateCode(AstBuilder astBuilder, ITextOutput output, DecompilationOptions options, IAstTransform additionalTransform = null)
		{
			astBuilder.RunTransformations(transformAbortCondition);
			if (additionalTransform != null) {
				additionalTransform.Run(astBuilder.CompilationUnit);
			}
			if (options.DecompilerSettings.ShowXmlDocumentation) {
				AddXmlDocTransform.Run(astBuilder.CompilationUnit);
			}
			astBuilder.GenerateCode(output);
		}

		public override void DecompileAssembly(LoadedAssembly assembly, ITextOutput output, DecompilationOptions options)
		{
			if (options.FullDecompilation && options.SaveAsProjectDirectory != null) {

                //Checks if must create a solution
                if (options.CreateSolution)
                {
                    //Solution directory
                    var solutionDir = options.SaveAsProjectDirectory;
                    
                    //List of the project names and their guid
                    List<Tuple<string, string>> projects = new List<Tuple<string, string>>();
                    
                    //For each module
                    foreach (var module in assembly.AssemblyDefinition.Modules)
                    {
                        //Creates the project and the various files
                        var projectDir = Path.Combine(solutionDir, TextView.DecompilerTextView.CleanUpName(Path.GetFileNameWithoutExtension(module.Name)));
                        Directory.CreateDirectory(projectDir);
                        options.SaveAsProjectDirectory = projectDir;
                        HashSet<string> directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        var files = WriteCodeFilesInProject(module, options, directories).ToList();
                        files.AddRange(WriteResourceFilesInProject(module, options, directories));
                        using (var writer = new StreamWriter(Path.Combine(projectDir, Path.GetFileName(projectDir) + this.ProjectFileExtension), false, System.Text.Encoding.UTF8))
                            projects.Add(Tuple.Create(
                                Path.GetFileName(projectDir),
                                "{" + WriteProjectFile(writer, files, module, options).ToString().ToUpperInvariant() + "}"
                            ));
                    }

                    //Writes the solution
                    WriteSolutionFile(new TextOutputWriter(output), Enumerable.Reverse(projects));
                }
                else
                {
                    HashSet<string> directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var files = assembly.AssemblyDefinition.Modules.SelectMany(m => WriteCodeFilesInProject(m, options, directories)).ToList();
                    files.AddRange(assembly.AssemblyDefinition.Modules.SelectMany(m => WriteResourceFilesInProject(m, options, directories)));
                    WriteProjectFile(new TextOutputWriter(output), files, assembly.AssemblyDefinition.MainModule, options);
                }

			} else {
				base.DecompileAssembly(assembly, output, options);
				output.WriteLine();
                output.WriteLine("// Main module:");
                ModuleDefinition mainModule = assembly.AssemblyDefinition.MainModule;
                DecompileModule(mainModule, output, options);
				output.WriteLine();
				
				// don't automatically load additional assemblies when an assembly node is selected in the tree view
				using (options.FullDecompilation ? null : LoadedAssembly.DisableAssemblyLoad()) {
					AstBuilder codeDomBuilder = CreateAstBuilder(options, currentModule: assembly.AssemblyDefinition.MainModule);
					codeDomBuilder.AddAssembly(assembly.AssemblyDefinition, onlyAssemblyLevel: !options.FullDecompilation);
					codeDomBuilder.RunTransformations(transformAbortCondition);
					codeDomBuilder.GenerateCode(output);
				}
			}
			OnDecompilationFinished(null);
		}

        public override void DecompileModule(ModuleDefinition module, ITextOutput output, DecompilationOptions options)
        {
            base.DecompileModule(module, output, options);
            if (module.EntryPoint != null)
            {
                output.Write("// Entry point: ");
                output.WriteReference(module.EntryPoint.DeclaringType.FullName + "." + module.EntryPoint.Name, module.EntryPoint);
                output.WriteLine();
            }
            switch (module.Architecture)
            {
                case TargetArchitecture.I386:
                    if ((module.Attributes & ModuleAttributes.Required32Bit) == ModuleAttributes.Required32Bit)
                        output.WriteLine("// Architecture: x86");
                    else
                        output.WriteLine("// Architecture: AnyCPU");
                    break;
                case TargetArchitecture.AMD64:
                    output.WriteLine("// Architecture: x64");
                    break;
                case TargetArchitecture.IA64:
                    output.WriteLine("// Architecture: Itanium-64");
                    break;
            }
            if ((module.Attributes & ModuleAttributes.ILOnly) == 0)
            {
                output.WriteLine("// This assembly contains unmanaged code.");
            }
            switch (module.Runtime)
            {
                case TargetRuntime.Net_1_0:
                    output.WriteLine("// Runtime: .NET 1.0");
                    break;
                case TargetRuntime.Net_1_1:
                    output.WriteLine("// Runtime: .NET 1.1");
                    break;
                case TargetRuntime.Net_2_0:
                    output.WriteLine("// Runtime: .NET 2.0");
                    break;
                case TargetRuntime.Net_4_0:
                    output.WriteLine("// Runtime: .NET 4.0");
                    break;
            }
        }

        #region WriteSolutionFile
        private void WriteSolutionFile(TextWriter writer, IEnumerable<Tuple<string, string>> projects)
        {
            writer.WriteLine();
            writer.WriteLine("Microsoft Visual Studio Solution File, Format Version 11.00");
            writer.WriteLine("# Visual Studio 2010");
            foreach (var proj in projects)
            {
                writer.WriteLine(
                    "Project(\"{0}\") = \"{1}\", \"{1}\\{1}{2}\", \"{3}\"",
                    "{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}",
                    proj.Item1,
                    this.ProjectFileExtension,
                    proj.Item2
                );
                writer.WriteLine("EndProject");
            }
            writer.WriteLine("Global");
	        writer.WriteLine("    GlobalSection(SolutionConfigurationPlatforms) = preSolution");
		    writer.WriteLine("        Debug|Any CPU = Debug|Any CPU");
		    writer.WriteLine("        Release|Any CPU = Release|Any CPU");
	        writer.WriteLine("    EndGlobalSection");
            writer.WriteLine("    GlobalSection(ProjectConfigurationPlatforms) = postSolution");
            foreach (var proj in projects)
            {
                writer.WriteLine("        {0}.Debug|Any CPU.ActiveCfg = Debug|Any CPU", proj.Item2);
		        writer.WriteLine("        {0}.Debug|Any CPU.Build.0 = Debug|Any CPU", proj.Item2);
		        writer.WriteLine("        {0}.Release|Any CPU.ActiveCfg = Release|Any CPU", proj.Item2);
                writer.WriteLine("        {0}.Release|Any CPU.Build.0 = Release|Any CPU", proj.Item2);
            }
            writer.WriteLine("    EndGlobalSection");
	        writer.WriteLine("    GlobalSection(SolutionProperties) = preSolution");
		    writer.WriteLine("        HideSolutionNode = FALSE");
	        writer.WriteLine("    EndGlobalSection");
            writer.WriteLine("EndGlobal");
        }
        #endregion

        #region WriteProjectFile
        Guid WriteProjectFile(TextWriter writer, IEnumerable<Tuple<string, string>> files, ModuleDefinition module, DecompilationOptions options)
		{
            Guid returnGuid;
			const string ns = "http://schemas.microsoft.com/developer/msbuild/2003";
			string platformName;
			switch (module.Architecture) {
				case TargetArchitecture.I386:
					if ((module.Attributes & ModuleAttributes.Required32Bit) == ModuleAttributes.Required32Bit)
						platformName = "x86";
					else
						platformName = "AnyCPU";
					break;
				case TargetArchitecture.AMD64:
					platformName = "x64";
					break;
				case TargetArchitecture.IA64:
					platformName = "Itanium";
					break;
				default:
					throw new NotSupportedException("Invalid value for TargetArchitecture");
			}
			using (XmlTextWriter w = new XmlTextWriter(writer)) {
				w.Formatting = Formatting.Indented;
				w.WriteStartDocument();
				w.WriteStartElement("Project", ns);
				w.WriteAttributeString("ToolsVersion", "4.0");
				w.WriteAttributeString("DefaultTargets", "Build");

				w.WriteStartElement("PropertyGroup");
				w.WriteElementString("ProjectGuid", "{" + (returnGuid = Guid.NewGuid()).ToString().ToUpperInvariant() + "}");

				w.WriteStartElement("Configuration");
				w.WriteAttributeString("Condition", " '$(Configuration)' == '' ");
				w.WriteValue("Debug");
				w.WriteEndElement(); // </Configuration>

				w.WriteStartElement("Platform");
				w.WriteAttributeString("Condition", " '$(Platform)' == '' ");
				w.WriteValue(platformName);
				w.WriteEndElement(); // </Platform>

				switch (module.Kind) {
					case ModuleKind.Windows:
						w.WriteElementString("OutputType", "WinExe");
						break;
					case ModuleKind.Console:
						w.WriteElementString("OutputType", "Exe");
						break;
                    case ModuleKind.NetModule:
                        w.WriteElementString("OutputType", "Module");
                        break;
					default:
						w.WriteElementString("OutputType", "Library");
						break;
				}

                w.WriteElementString("AssemblyName", Path.GetFileNameWithoutExtension(module.Name));
				switch (module.Runtime) {
					case TargetRuntime.Net_1_0:
						w.WriteElementString("TargetFrameworkVersion", "v1.0");
						break;
					case TargetRuntime.Net_1_1:
						w.WriteElementString("TargetFrameworkVersion", "v1.1");
						break;
					case TargetRuntime.Net_2_0:
						w.WriteElementString("TargetFrameworkVersion", "v2.0");
						// TODO: Detect when .NET 3.0/3.5 is required
						break;
					default:
						w.WriteElementString("TargetFrameworkVersion", "v4.0");
						// TODO: Detect TargetFrameworkProfile
						break;
				}
				w.WriteElementString("WarningLevel", "4");

				w.WriteEndElement(); // </PropertyGroup>

				w.WriteStartElement("PropertyGroup"); // platform-specific
				w.WriteAttributeString("Condition", " '$(Platform)' == '" + platformName + "' ");
				w.WriteElementString("PlatformTarget", platformName);
				w.WriteEndElement(); // </PropertyGroup> (platform-specific)

				w.WriteStartElement("PropertyGroup"); // Debug
				w.WriteAttributeString("Condition", " '$(Configuration)' == 'Debug' ");
				w.WriteElementString("OutputPath",
                    (options.CreateSolution && !module.IsMain ? 
                        "..\\" + TextView.DecompilerTextView.CleanUpName(Path.GetFileNameWithoutExtension(module.Assembly.MainModule.Name)) + "\\" : 
                        string.Empty) +
                    "bin\\Debug\\");
				w.WriteElementString("DebugSymbols", "true");
				w.WriteElementString("DebugType", "full");
				w.WriteElementString("Optimize", "false");
				w.WriteEndElement(); // </PropertyGroup> (Debug)

				w.WriteStartElement("PropertyGroup"); // Release
				w.WriteAttributeString("Condition", " '$(Configuration)' == 'Release' ");
				w.WriteElementString("OutputPath",
                    (options.CreateSolution && !module.IsMain ?
                        "..\\" + TextView.DecompilerTextView.CleanUpName(Path.GetFileNameWithoutExtension(module.Assembly.MainModule.Name)) + "\\" :
                        string.Empty) +
                    "bin\\Release\\");
				w.WriteElementString("DebugSymbols", "true");
				w.WriteElementString("DebugType", "pdbonly");
				w.WriteElementString("Optimize", "true");
				w.WriteEndElement(); // </PropertyGroup> (Release)


				w.WriteStartElement("ItemGroup"); // References
				foreach (AssemblyNameReference r in module.AssemblyReferences) {
					if (r.Name != "mscorlib") {
						w.WriteStartElement("Reference");
						w.WriteAttributeString("Include", r.Name);
						// TODO: RequiredTargetFramework
						w.WriteEndElement();
					}
				}
				w.WriteEndElement(); // </ItemGroup> (References)

				foreach (IGrouping<string, string> gr in (from f in files group f.Item2 by f.Item1 into g orderby g.Key select g)) {
					w.WriteStartElement("ItemGroup");
					foreach (string file in gr.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)) {
						w.WriteStartElement(gr.Key);
						w.WriteAttributeString("Include", file);
						w.WriteEndElement();
					}
					w.WriteEndElement();
				}

                //Links to the other modules of the solution (if present)
                if (options.CreateSolution && module.IsMain)
                {
                    var otherModules = module.Assembly.Modules.Except(new[] { module }).ToArray();
                    if (otherModules.Length > 0)
                    {
                        w.WriteStartElement("ItemGroup");
                        foreach (var m in otherModules)
                        {
                            w.WriteStartElement("AddModules");
                            w.WriteAttributeString("Include", "bin\\$(Configuration)\\" + m.Name);
                            w.WriteEndElement();
                        }
                        w.WriteEndElement();
                    }
                }

				w.WriteStartElement("Import");
				w.WriteAttributeString("Project", "$(MSBuildToolsPath)\\Microsoft.CSharp.targets");
				w.WriteEndElement();

				w.WriteEndDocument();
			}

            //Returns the guid of the project
            return returnGuid;
		}
		#endregion

		#region WriteCodeFilesInProject
		bool IncludeTypeWhenDecompilingProject(TypeDefinition type, DecompilationOptions options)
		{
			if (type.Name == "<Module>" || AstBuilder.MemberIsHidden(type, options.DecompilerSettings))
				return false;
			if (type.Namespace == "XamlGeneratedNamespace" && type.Name == "GeneratedInternalTypeHelper")
				return false;
			return true;
		}

		IEnumerable<Tuple<string, string>> WriteCodeFilesInProject(ModuleDefinition module, DecompilationOptions options, HashSet<string> directories)
		{
            var files = module.Types.Where(t => IncludeTypeWhenDecompilingProject(t, options)).GroupBy(
				delegate(TypeDefinition type) {
					string file = TextView.DecompilerTextView.CleanUpName(type.Name) + this.FileExtension;
					if (string.IsNullOrEmpty(type.Namespace)) {
						return file;
					} else {
						string dir = TextView.DecompilerTextView.CleanUpName(type.Namespace);
						if (directories.Add(dir))
							Directory.CreateDirectory(Path.Combine(options.SaveAsProjectDirectory, dir));
						return Path.Combine(dir, file);
					}
				}, StringComparer.OrdinalIgnoreCase).ToList();
			AstMethodBodyBuilder.ClearUnhandledOpcodes();
			Parallel.ForEach(
				files,
				new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
				delegate(IGrouping<string, TypeDefinition> file) {
					using (StreamWriter w = new StreamWriter(Path.Combine(options.SaveAsProjectDirectory, file.Key))) {
						AstBuilder codeDomBuilder = CreateAstBuilder(options, currentModule: module);
						foreach (TypeDefinition type in file) {
							codeDomBuilder.AddType(type);
						}
						codeDomBuilder.RunTransformations(transformAbortCondition);
						codeDomBuilder.GenerateCode(new PlainTextOutput(w));
					}
				});
			AstMethodBodyBuilder.PrintNumberOfUnhandledOpcodes();
			return files.Select(f => Tuple.Create("Compile", f.Key));
		}
		#endregion

		#region WriteResourceFilesInProject
		IEnumerable<Tuple<string, string>> WriteResourceFilesInProject(ModuleDefinition module, DecompilationOptions options, HashSet<string> directories)
		{
			AppDomain bamlDecompilerAppDomain = null;
			try {
				foreach (EmbeddedResource r in module.Resources.OfType<EmbeddedResource>()) {
					string fileName;
					Stream s = r.GetResourceStream();
					s.Position = 0;
					if (r.Name.EndsWith(".g.resources", StringComparison.OrdinalIgnoreCase)) {
						IEnumerable<DictionaryEntry> rs = null;
						try {
							rs = new ResourceSet(s).Cast<DictionaryEntry>();
						}
						catch (ArgumentException) {
						}
						if (rs != null && rs.All(e => e.Value is Stream)) {
							foreach (var pair in rs) {
								fileName = Path.Combine(((string)pair.Key).Split('/').Select(p => TextView.DecompilerTextView.CleanUpName(p)).ToArray());
								string dirName = Path.GetDirectoryName(fileName);
								if (!string.IsNullOrEmpty(dirName) && directories.Add(dirName)) {
									Directory.CreateDirectory(Path.Combine(options.SaveAsProjectDirectory, dirName));
								}
								Stream entryStream = (Stream)pair.Value;
								entryStream.Position = 0;
								if (fileName.EndsWith(".baml", StringComparison.OrdinalIgnoreCase)) {
									MemoryStream ms = new MemoryStream();
									entryStream.CopyTo(ms);
									// TODO implement extension point
//									var decompiler = Baml.BamlResourceEntryNode.CreateBamlDecompilerInAppDomain(ref bamlDecompilerAppDomain, assembly.FileName);
//									string xaml = null;
//									try {
//										xaml = decompiler.DecompileBaml(ms, assembly.FileName, new ConnectMethodDecompiler(assembly), new AssemblyResolver(assembly));
//									}
//									catch (XamlXmlWriterException) { } // ignore XAML writer exceptions
//									if (xaml != null) {
//										File.WriteAllText(Path.Combine(options.SaveAsProjectDirectory, Path.ChangeExtension(fileName, ".xaml")), xaml);
//										yield return Tuple.Create("Page", Path.ChangeExtension(fileName, ".xaml"));
//										continue;
//									}
								}
								using (FileStream fs = new FileStream(Path.Combine(options.SaveAsProjectDirectory, fileName), FileMode.Create, FileAccess.Write)) {
									entryStream.CopyTo(fs);
								}
								yield return Tuple.Create("Resource", fileName);
							}
							continue;
						}
					}
					fileName = GetFileNameForResource(r.Name, directories);
					using (FileStream fs = new FileStream(Path.Combine(options.SaveAsProjectDirectory, fileName), FileMode.Create, FileAccess.Write)) {
						s.CopyTo(fs);
					}
					yield return Tuple.Create("EmbeddedResource", fileName);
				}
			}
			finally {
				if (bamlDecompilerAppDomain != null)
					AppDomain.Unload(bamlDecompilerAppDomain);
			}
		}

		string GetFileNameForResource(string fullName, HashSet<string> directories)
		{
			string[] splitName = fullName.Split('.');
			string fileName = TextView.DecompilerTextView.CleanUpName(fullName);
			for (int i = splitName.Length - 1; i > 0; i--) {
				string ns = string.Join(".", splitName, 0, i);
				if (directories.Contains(ns)) {
					string name = string.Join(".", splitName, i, splitName.Length - i);
					fileName = Path.Combine(ns, TextView.DecompilerTextView.CleanUpName(name));
					break;
				}
			}
			return fileName;
		}
		#endregion

		AstBuilder CreateAstBuilder(DecompilationOptions options, ModuleDefinition currentModule = null, TypeDefinition currentType = null, bool isSingleMember = false)
		{
			if (currentModule == null)
				currentModule = currentType.Module;
			DecompilerSettings settings = options.DecompilerSettings;
			if (isSingleMember) {
				settings = settings.Clone();
				settings.UsingDeclarations = false;
			}
			return new AstBuilder(
				new DecompilerContext(currentModule) {
					CancellationToken = options.CancellationToken,
					CurrentType = currentType,
					Settings = settings
				});
		}

		public override string TypeToString(TypeReference type, bool includeNamespace, ICustomAttributeProvider typeAttributes = null)
		{
			ConvertTypeOptions options = ConvertTypeOptions.IncludeTypeParameterDefinitions;
			if (includeNamespace)
				options |= ConvertTypeOptions.IncludeNamespace;

			return TypeToString(options, type, typeAttributes);
		}

		string TypeToString(ConvertTypeOptions options, TypeReference type, ICustomAttributeProvider typeAttributes = null)
		{
			AstType astType = AstBuilder.ConvertType(type, typeAttributes, options);

			StringWriter w = new StringWriter();
			if (type.IsByReference) {
				ParameterDefinition pd = typeAttributes as ParameterDefinition;
				if (pd != null && (!pd.IsIn && pd.IsOut))
					w.Write("out ");
				else
					w.Write("ref ");

				if (astType is ComposedType && ((ComposedType)astType).PointerRank > 0)
					((ComposedType)astType).PointerRank--;
			}

			astType.AcceptVisitor(new OutputVisitor(w, new CSharpFormattingOptions()), null);
			return w.ToString();
		}

		public override string FormatPropertyName(PropertyDefinition property, bool? isIndexer)
		{
			if (property == null)
				throw new ArgumentNullException("property");

			if (!isIndexer.HasValue) {
				isIndexer = property.IsIndexer();
			}
			if (isIndexer.Value) {
				var buffer = new System.Text.StringBuilder();
				var accessor = property.GetMethod ?? property.SetMethod;
				if (accessor.HasOverrides) {
					var declaringType = accessor.Overrides.First().DeclaringType;
					buffer.Append(TypeToString(declaringType, includeNamespace: true));
					buffer.Append(@".");
				}
				buffer.Append(@"this[");
				bool addSeparator = false;
				foreach (var p in property.Parameters) {
					if (addSeparator)
						buffer.Append(@", ");
					else
						addSeparator = true;
					buffer.Append(TypeToString(p.ParameterType, includeNamespace: true));
				}
				buffer.Append(@"]");
				return buffer.ToString();
			} else
				return property.Name;
		}
		
		public override string FormatTypeName(TypeDefinition type)
		{
			if (type == null)
				throw new ArgumentNullException("type");
			
			return TypeToString(ConvertTypeOptions.DoNotUsePrimitiveTypeNames | ConvertTypeOptions.IncludeTypeParameterDefinitions, type);
		}

		public override bool ShowMember(MemberReference member)
		{
			return showAllMembers || !AstBuilder.MemberIsHidden(member, new DecompilationOptions().DecompilerSettings);
		}

		public override MemberReference GetOriginalCodeLocation(MemberReference member)
		{
			if (showAllMembers || !DecompilerSettingsPanel.CurrentDecompilerSettings.AnonymousMethods)
				return member;
			else
				return ICSharpCode.ILSpy.TreeNodes.Analyzer.Helpers.GetOriginalCodeLocation(member);
		}

		public override string GetTooltip(MemberReference member)
		{
			MethodDefinition md = member as MethodDefinition;
			PropertyDefinition pd = member as PropertyDefinition;
			EventDefinition ed = member as EventDefinition;
			FieldDefinition fd = member as FieldDefinition;
			if (md != null || pd != null || ed != null || fd != null) {
				AstBuilder b = new AstBuilder(new DecompilerContext(member.Module) { Settings = new DecompilerSettings { UsingDeclarations = false } });
				b.DecompileMethodBodies = false;
				if (md != null)
					b.AddMethod(md);
				else if (pd != null)
					b.AddProperty(pd);
				else if (ed != null)
					b.AddEvent(ed);
				else
					b.AddField(fd);
				b.RunTransformations();
				foreach (var attribute in b.CompilationUnit.Descendants.OfType<AttributeSection>())
					attribute.Remove();

				StringWriter w = new StringWriter();
				b.GenerateCode(new PlainTextOutput(w));
				return Regex.Replace(w.ToString(), @"\s+", " ").TrimEnd();
			}

			return base.GetTooltip(member);
		}
	}
}
