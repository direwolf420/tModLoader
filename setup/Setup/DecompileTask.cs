﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.OutputVisitor;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using Terraria.ModLoader.Properties;
using static Terraria.ModLoader.Setup.Program;

namespace Terraria.ModLoader.Setup
{
	public class DecompileTask : SetupOperation
	{
		private class EmbeddedAssemblyResolver : IAssemblyResolver
		{
			private readonly PEFile baseModule;
			private readonly UniversalAssemblyResolver _resolver;
			private readonly Dictionary<string, PEFile> cache = new Dictionary<string, PEFile>();

			public EmbeddedAssemblyResolver(PEFile baseModule, string targetFramework)
			{
				this.baseModule = baseModule;
				_resolver = new UniversalAssemblyResolver(baseModule.FileName, true, targetFramework, PEStreamOptions.PrefetchMetadata);
				_resolver.AddSearchDirectory(Path.GetDirectoryName(baseModule.FileName));
			}

			public PEFile Resolve(IAssemblyReference name)
			{
				lock (this)
				{
					if (cache.TryGetValue(name.FullName, out var module))
						return module;
					
					//look in the base module's embedded resources
					var resName = name.Name + ".dll";
					var res = baseModule.Resources.Where(r => r.ResourceType == ResourceType.Embedded).SingleOrDefault(r => r.Name.EndsWith(resName));
					if (!res.IsNil)
						module = new PEFile(res.Name, res.TryOpenStream());

					if (module == null)
						module = _resolver.Resolve(name);
					
					cache[name.FullName] = module;
					return module;
				}
			}

			public PEFile ResolveModule(PEFile mainModule, string moduleName) => _resolver.ResolveModule(mainModule, moduleName);
		}

		private class ExtendedProjectDecompiler : WholeProjectDecompiler
		{
			public new bool IncludeTypeWhenDecompilingProject(PEFile module, TypeDefinitionHandle type) => base.IncludeTypeWhenDecompilingProject(module, type);
		}

		public static readonly Version clientVersion = new Version(Settings.Default.ClientVersion);
		public static readonly Version serverVersion = new Version(Settings.Default.ServerVersion);

		private readonly string srcDir;
		private readonly bool serverOnly;
		private readonly bool formatOutput = Settings.Default.FormatAfterDecompiling;

		private ExtendedProjectDecompiler projectDecompiler;

		private readonly DecompilerSettings decompilerSettings = new DecompilerSettings(LanguageVersion.Latest)
		{
			RemoveDeadCode = true,
			CSharpFormattingOptions = FormattingOptionsFactory.CreateKRStyle()
		};

		public DecompileTask(ITaskInterface taskInterface, string srcDir, bool serverOnly = false) : base(taskInterface)
		{
			this.srcDir = srcDir;
			this.serverOnly = serverOnly;
		}

		public override bool ConfigurationDialog()
		{
			if (File.Exists(TerrariaPath) && File.Exists(TerrariaServerPath))
				return true;

			return (bool) taskInterface.Invoke(new Func<bool>(SelectTerrariaDialog));
		}

		public override void Run()
		{
			taskInterface.SetStatus("Deleting Old Src");
			if (Directory.Exists(srcDir))
				Directory.Delete(srcDir, true);
			
			var clientModule = serverOnly ? null : ReadModule(TerrariaPath, clientVersion);
			var serverModule = ReadModule(TerrariaServerPath, serverVersion);
			var mainModule = serverOnly ? serverModule : clientModule;

			projectDecompiler = new ExtendedProjectDecompiler { 
				Settings = decompilerSettings,
				AssemblyResolver = new EmbeddedAssemblyResolver(mainModule, mainModule.Reader.DetectTargetFrameworkId())
			};


			var items = new List<WorkItem>();
			var files = new HashSet<string>();
			var resources = new HashSet<string>();

			if (!serverOnly)
				AddModule(clientModule, projectDecompiler.AssemblyResolver, items, files, resources);

			AddModule(serverModule, projectDecompiler.AssemblyResolver, items, files, resources, serverOnly ? null : "SERVER");

			items.Add(WriteProjectFile(mainModule, files, resources));

			ExecuteParallel(items);
		}

		protected PEFile ReadModule(string path, Version version)
		{
			var versionedPath = path.Insert(path.LastIndexOf('.'), $"_v{version}");
			if (File.Exists(versionedPath))
				path = versionedPath;

			taskInterface.SetStatus("Loading " + Path.GetFileName(path));
			using (var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read))
			{
				var module = new PEFile(path, fileStream, PEStreamOptions.PrefetchEntireImage);
				var assemblyName = new AssemblyName(module.FullName);
				if (assemblyName.Version != version)
					throw new Exception($"{assemblyName.Name} version {assemblyName.Version}. Expected {version}");

				return module;
			}
		}

		private IEnumerable<IGrouping<string, TypeDefinitionHandle>> GetCodeFiles(PEFile module)
		{
			var metadata = module.Metadata;
			return module.Metadata.GetTopLevelTypeDefinitions().Where(td => projectDecompiler.IncludeTypeWhenDecompilingProject(module, td))
				.GroupBy(h =>
				{
					var type = metadata.GetTypeDefinition(h);
					var path = WholeProjectDecompiler.CleanUpFileName(metadata.GetString(type.Name)) + ".cs";
					if (!string.IsNullOrEmpty(metadata.GetString(type.Namespace)))
						path = Path.Combine(WholeProjectDecompiler.CleanUpFileName(metadata.GetString(type.Namespace)), path);
					return path.Replace('\\', '/');
				}, StringComparer.OrdinalIgnoreCase);
		}

		private static IEnumerable<(string path, Resource r)> GetResourceFiles(PEFile module)
		{
			return module.Resources.Where(r => r.ResourceType == ResourceType.Embedded).Select(res =>
			{
				var path = res.Name;
				path = path.Replace("Terraria.Libraries.", "Terraria.Libraries\\");
				path = path.Replace("Terraria.Localization.Content.", "Terraria.Localization.Content\\");
				if (path.EndsWith(".dll"))
				{
					var asmRef = module.AssemblyReferences.SingleOrDefault(r => path.EndsWith(r.Name + ".dll"));
					if (asmRef != null)
						path = Path.Combine(path.Substring(0, path.Length - asmRef.Name.Length - 5), asmRef.Name + ".dll");
				}
				else if (IsCultureFile(path))
					path = path.Insert(path.LastIndexOf('.'), ".Main");

				return (path.Replace('\\', '/'), res);
			});
		}

		private static bool IsCultureFile(string path) {
			try {
				CultureInfo.GetCultureInfo(Path.GetFileNameWithoutExtension(path));
				return true;
			}
			catch (CultureNotFoundException) { }
			return false;
		}

		private DecompilerTypeSystem AddModule(PEFile module, IAssemblyResolver resolver, List<WorkItem> items, ISet<string> sourceSet, ISet<string> resourceSet, string conditional = null)
		{
			var sources = GetCodeFiles(module).ToList();
			var resources = GetResourceFiles(module).ToList();

			var ts = new DecompilerTypeSystem(module, resolver, decompilerSettings);
			items.AddRange(sources
				.Where(src => sourceSet.Add(src.Key))
				.Select(src => DecompileSourceFile(ts, src, conditional)));

			if (conditional != null && resources.Any(res => !resourceSet.Contains(res.path)))
				throw new Exception($"Conditional ({conditional}) resources not supported");

			items.AddRange(resources
				.Where(res => resourceSet.Add(res.path))
				.Select(res => ExtractResource(res.path, res.r)));

			return ts;
		}

		private WorkItem ExtractResource(string name, Resource res)
		{
			return new WorkItem("Extracting: " + name, () =>
			{
				var path = Path.Combine(srcDir, name);
				CreateParentDirectory(path);

				var s = res.TryOpenStream();
				s.Position = 0;
				using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
					s.CopyTo(fs);
			});
		}

		private CSharpDecompiler CreateDecompiler(DecompilerTypeSystem ts)
		{
			var decompiler = new CSharpDecompiler(ts, projectDecompiler.Settings)
			{
				CancellationToken = taskInterface.CancellationToken
			};
			decompiler.AstTransforms.Add(new EscapeInvalidIdentifiers());
			decompiler.AstTransforms.Add(new RemoveCLSCompliantAttribute());
			return decompiler;
		}

		private WorkItem DecompileSourceFile(DecompilerTypeSystem ts, IGrouping<string, TypeDefinitionHandle> src, string conditional = null)
		{
			return new WorkItem("Decompiling: " + src.Key, updateStatus =>
			{
				var path = Path.Combine(srcDir, src.Key);
				CreateParentDirectory(path);

				using (var w = new StringWriter())
				{
					if (conditional != null)
						w.WriteLine("#if "+conditional);

					CreateDecompiler(ts)
						.DecompileTypes(src.ToArray())
						.AcceptVisitor(new CSharpOutputVisitor(w, projectDecompiler.Settings.CSharpFormattingOptions));

					if (conditional != null)
						w.WriteLine("#endif");

					string source = w.ToString();
					if (formatOutput) {
						updateStatus("Formatting: " + src.Key);
						source = FormatTask.Format(source, taskInterface.CancellationToken, true);
					}

					File.WriteAllText(path, source);
				}
			});
		}

		private WorkItem WriteProjectFile(PEFile module, IEnumerable<string> sources, IEnumerable<string> resources)
		{
			var name = Path.GetFileNameWithoutExtension(module.Name) + ".csproj";
			return new WorkItem("Writing: " + name, () =>
			{
				var path = Path.Combine(srcDir, name);
				CreateParentDirectory(path);

				using (var sw = new StreamWriter(path))
				using (var w = new XmlTextWriter(sw)) {
					w.Formatting = System.Xml.Formatting.Indented;
					w.WriteStartElement("Project");
					w.WriteAttributeString("Sdk", "Microsoft.NET.Sdk");

					w.WriteStartElement("PropertyGroup");
					w.WriteElementString("TargetFramework", "net40");
					w.WriteElementString("OutputType", "WinExe");
					w.WriteElementString("Version", new AssemblyName(module.FullName).Version.ToString());
					
					var attribs = GetCustomAttributes(module);
					w.WriteElementString("Company", attribs[nameof(AssemblyCompanyAttribute)]);
					w.WriteElementString("Copyright", attribs[nameof(AssemblyCopyrightAttribute)]);

					w.WriteElementString("RootNamespace", "");
					w.WriteElementString("Configurations", "Debug;Release;ServerDebug;ServerRelease");

					w.WriteElementString("AssemblySearchPaths", "$(AssemblySearchPaths);{GAC}");
					w.WriteElementString("PlatformTarget", "x86");
					w.WriteElementString("AllowUnsafeBlocks", "true");
					w.WriteElementString("Optimize", "true");
					w.WriteEndElement(); // </PropertyGroup>

					//configurations
					w.WriteStartElement("PropertyGroup");
					w.WriteAttributeString("Condition", "$(Configuration.Contains('Server'))");
					w.WriteElementString("OutputType", "Exe");
					w.WriteElementString("DefineConstants", "$(DefineConstants);SERVER");
					w.WriteElementString("OutputName", "$(OutputName)Server");
					w.WriteEndElement(); // </PropertyGroup>

					w.WriteStartElement("PropertyGroup");
					w.WriteAttributeString("Condition", "!$(Configuration.Contains('Server'))");
					w.WriteElementString("DefineConstants", "$(DefineConstants);CLIENT");
					w.WriteEndElement(); // </PropertyGroup>

					w.WriteStartElement("PropertyGroup");
					w.WriteAttributeString("Condition", "$(Configuration.Contains('Debug'))");
					w.WriteElementString("Optimize", "false");
					w.WriteElementString("DefineConstants", "$(DefineConstants);DEBUG");
					w.WriteEndElement(); // </PropertyGroup>

					// references
					w.WriteStartElement("ItemGroup");
					foreach (var r in module.AssemblyReferences.OrderBy(r => r.Name)) {
						if (r.Name == "mscorlib") continue;

						w.WriteStartElement("Reference");
						w.WriteAttributeString("Include", r.Name);
						w.WriteEndElement();
					}
					w.WriteEndElement(); // </ItemGroup>
					
					// resources
					w.WriteStartElement("ItemGroup");
					foreach (var r in ApplyWildcards(resources, sources.ToArray()).OrderBy(r => r)) {
						w.WriteStartElement("EmbeddedResource");
						w.WriteAttributeString("Include", r);
						w.WriteEndElement();
					}
					w.WriteEndElement(); // </ItemGroup>
					w.WriteEndElement(); // </Project>

					sw.Write(Environment.NewLine);
				}
			});
		}

		private IEnumerable<string> ApplyWildcards(IEnumerable<string> include, IReadOnlyList<string> exclude) {
			var wildpaths = new HashSet<string>();
			foreach (var path in include) {
				if (wildpaths.Any(path.StartsWith))
					continue;

				string wpath = path;
				string cards = "";
				while (wpath.Contains('/')) {
					var parent = wpath.Substring(0, wpath.LastIndexOf('/'));
					if (exclude.Any(e => e.StartsWith(parent)))
						break; //can't use parent as a wildcard

					wpath = parent;
					if (cards.Length < 2)
						cards += "*";
				}

				if (wpath != path) {
					wildpaths.Add(wpath);
					yield return $"{wpath}/{cards}";
				} else {
					yield return path;
				}
			}
		}

		private static string[] knownAttributes = {nameof(AssemblyCompanyAttribute), nameof(AssemblyCopyrightAttribute) };
		private IDictionary<string, string> GetCustomAttributes(PEFile module) {
			var dict = new Dictionary<string, string>();

			var reader = module.Reader.GetMetadataReader();
			var attribs = reader.GetAssemblyDefinition().GetCustomAttributes().Select(reader.GetCustomAttribute);
			foreach (var attrib in attribs) {
				var ctor = reader.GetMemberReference((MemberReferenceHandle)attrib.Constructor);
				var attrTypeName = reader.GetString(reader.GetTypeReference((TypeReferenceHandle)ctor.Parent).Name);
				if (!knownAttributes.Contains(attrTypeName))
					continue;

				var value = attrib.DecodeValue(new IDGAFAttributeTypeProvider());
				dict[attrTypeName] = value.FixedArguments.Single().Value as string;
			}

			return dict;
		}

		private class IDGAFAttributeTypeProvider : ICustomAttributeTypeProvider<object>
		{
			public object GetPrimitiveType(PrimitiveTypeCode typeCode) => null;
			public object GetSystemType() => throw new NotImplementedException();
			public object GetSZArrayType(object elementType) => throw new NotImplementedException();
			public object GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => throw new NotImplementedException();
			public object GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => throw new NotImplementedException();
			public object GetTypeFromSerializedName(string name) => throw new NotImplementedException();
			public PrimitiveTypeCode GetUnderlyingEnumType(object type) => throw new NotImplementedException();
			public bool IsSystemType(object type) => throw new NotImplementedException();
		}
	}
}