using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Azure.Functions.Cli.Common;
using Azure.Functions.Cli.Helpers;
using Azure.Functions.Cli.Interfaces;
using Azure.Functions.Cli.Kubernetes;
using Azure.Functions.Cli.Kubernetes.Models;
using Colors.Net;
using Fclp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Azure.Functions.Cli.Actions.KubernetesActions
{
    [Action(Name = "deploy", Context = Context.Kubernetes, HelpText = "")]
    class KubernetesDeployAction : BaseAction
    {
        private readonly ISecretsManager _secretsManager;

        public string Registry { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Namespace { get; set; } = "default";
        public string PullSecret { get; set; } = string.Empty;
        public bool NoDocker { get; set; }
        public bool UseConfigMap { get; set; }
        public bool DryRun { get; private set; }
        // public OutputSerializationOptions OutputFormat { get; private set; }
        public string ImageName { get; private set; }
        public string ConfigMapName { get; private set; }
        public string SecretsCollectionName { get; private set; }
        public int? PollingInterval { get; private set; }
        public int? CooldownPeriod { get; private set; }

        public KubernetesDeployAction(ISecretsManager secretsManager)
        {
            _secretsManager = secretsManager;
        }

        public override ICommandLineParserResult ParseArgs(string[] args)
        {
            SetFlag<string>("name", "The name used for the deployment and other artifacts in kubernetes", n => Name = n, isRequired: true);
            SetFlag<string>("image-name", "Image to use for the pod deployment and to read functions from", n => ImageName = n);
            SetFlag<string>("registry", "When set, a docker build is run and an image is pused to that registry/name. This is mutually exclusive with --image-name. For docker hub, use username.", r => Registry = r);
            SetFlag<string>("namespace", "Kubernetes namespace to deploy to. Default: default", ns => Namespace = ns);
            SetFlag<string>("pull-secret", "The secret holding a private registry credentials", s => PullSecret = s);
            SetFlag<int>("polling-interval", "The polling interval for checking non-http triggers. Default: 30 (seconds)", p => PollingInterval = p);
            SetFlag<int>("cooldown-period", "The cooldown period for the deployment before scaling back to 0 after all triggers are no longer active. Default: 300 (seconds)", p => CooldownPeriod = p);
            SetFlag<string>("secrets-name", "The name of a secrets collection to use in the deployment instead of generating one based on local.settings.json", sn => SecretsCollectionName = sn);
            SetFlag<string>("config-map-name", "The name of a config map to use in the deployment", cm => ConfigMapName = cm);
            SetFlag<bool>("no-docker", "with --image-name, the core-tools will inspect the functions inside the image. This will require mounting the image filesystem. Passing --no-docker uses current directory for functions.", nd => NoDocker = nd);
            SetFlag<bool>("use-config-map", "local.settings.json will be creates as Secret/V1 object. This will create is as a ConfigMap instead.", c => UseConfigMap = c);
            SetFlag<bool>("dry-run", "Show the deployment template", f => DryRun = f);
            // SetFlag<OutputSerializationOptions>("output", "With --dry-run. Prints deployment in json, yaml or helm. Default: yaml", o => OutputFormat = o);
            return base.ParseArgs(args);
        }

        public override async Task RunAsync()
        {
            (var resolvedImageName, var shouldBuild) = ResolveImageName();
            TriggersPayload triggers = null;

            if (DryRun)
            {
                if (shouldBuild)
                {
                    // don't build on a --dry-run.
                    // read files from the local dir
                    triggers = await GetTriggersLocalFiles();
                }
                else
                {
                    triggers = await DockerHelpers.GetTriggersFromDockerImage(resolvedImageName);
                }
            }
            else
            {
                if (shouldBuild)
                {
                    await DockerHelpers.DockerBuild(resolvedImageName, Environment.CurrentDirectory);
                }
                triggers = await DockerHelpers.GetTriggersFromDockerImage(resolvedImageName);
            }

            var resources = KubernetesHelper.GetFunctionsDeploymentResources(Name, resolvedImageName, Namespace, triggers, _secretsManager.GetSecrets(), PullSecret, SecretsCollectionName, ConfigMapName, UseConfigMap, PollingInterval, CooldownPeriod);

            if (DryRun)
            {
                ColoredConsole.WriteLine(KubernetesHelper.SerializeResources(resources, OutputSerializationOptions.Yaml));
            }
            else
            {
                if (!await KubernetesHelper.NamespaceExists(Namespace))
                {
                    await KubernetesHelper.CreateNamespace(Namespace);
                }

                if (shouldBuild)
                {
                    await DockerHelpers.DockerPush(resolvedImageName);
                }

                foreach (var resource in resources)
                {
                    await KubectlHelper.KubectlApply(resource, showOutput: true, @namespace: Namespace);
                }
            }
        }

        private async Task<TriggersPayload> GetTriggersLocalFiles()
        {
            var functionsPath = Environment.CurrentDirectory;
            if (GlobalCoreToolsSettings.CurrentWorkerRuntime == WorkerRuntime.dotnet)
            {
                if (DotnetHelpers.CanDotnetBuild())
                {
                    var outputPath = Path.Combine("bin", "output");
                    await DotnetHelpers.BuildDotnetProject(outputPath, string.Empty, showOutput: false);
                    functionsPath = Path.Combine(Environment.CurrentDirectory, outputPath);
                }
            }

            var functionJsonFiles = FileSystemHelpers
                    .GetDirectories(functionsPath)
                    .Select(d => Path.Combine(d, "function.json"))
                    .Where(FileSystemHelpers.FileExists)
                    .Select(f => (filePath: f, content: FileSystemHelpers.ReadAllTextFromFile(f)));

            var functionsJsons = functionJsonFiles
                .Select(t => (filePath: t.filePath, jObject: JsonConvert.DeserializeObject<JObject>(t.content)))
                .Where(b => b.jObject["bindings"] != null)
                .ToDictionary(k => Path.GetFileName(Path.GetDirectoryName(k.filePath)), v => v.jObject);

            var hostJson = JsonConvert.DeserializeObject<JObject>(FileSystemHelpers.ReadAllTextFromFile("host.json"));

            return new TriggersPayload
            {
                HostJson = hostJson,
                FunctionsJson = functionsJsons
            };
        }

        private (string, bool) ResolveImageName()
        {
            if (!string.IsNullOrEmpty(Registry))
            {
                return ($"{Registry}/{Name}", true);
            }
            else if (!string.IsNullOrEmpty(ImageName))
            {
                return (ImageName, false);
            }
            throw new CliArgumentsException("either --image-name or --registry is required.");
        }
    }
}