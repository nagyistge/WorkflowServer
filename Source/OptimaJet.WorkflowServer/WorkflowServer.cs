﻿using AsyncHttp.Server;
using MongoDB.Driver;
using OptimaJet.Workflow;
using OptimaJet.Workflow.Core.Builder;
using OptimaJet.Workflow.Core.Bus;
using OptimaJet.Workflow.Core.Parser;
using OptimaJet.Workflow.Core.Runtime;
using OptimaJet.Workflow.RavenDB;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Xml.Linq;
using OptimaJet.Workflow.Oracle;
using JsonConvert = Newtonsoft.Json.JsonConvert;

namespace OptimaJet
{
    public delegate void LogDelegate(string msg);

    public class WorkflowServerParameter
    {
        public Guid RuntimeId;
        public string Provider;
        public string ConnectionString;
        public string Url;
        public string Database;
        public string ApiKey;
        public LogDelegate Log;

        public string CallbackApiUrl { get; set; }

        public bool CallbackGenScheme { get; set; }

        public string DBUrl { get; set; }

        public bool NoStartWorkflow { get; set; }

        public string BackendFolder { get; set; }
    }

    public class WorkflowServer
    {
        private WorkflowRuntime _runtime;
        public WorkflowServerParameter Parameters;
        public WorkflowCallbackProvider callbackProvider;

        public WorkflowServer(WorkflowServerParameter parameters)
        {
            Parameters = parameters;

            switch (Parameters.Provider)
            {
                case "mssql":
                    _runtime = CreateRuntimeMSSQL();
                    break;
                case "oracle":
                    _runtime = CreateRuntimeOracle();
                    break;
                case "mysql":
                    _runtime = CreateRuntimeMySQL();
                    break;
                case "postgresql":
                    _runtime = CreateRuntimePostgreSQL();
                    break;
                case "ravendb":
                    _runtime = CreateRuntimeRavenDB();
                    break;
                case "mongodb":
                    _runtime = CreateRuntimeMongoDB();
                    break;
                default:
                    throw new Exception(string.Format("Provider = '{0}' is not support", Parameters.Provider));
            }

            _runtime.WithBus(new NullBus())
                .WithTimerManager(new TimerManager())
                .WithActionProvider(callbackProvider)
                .WithRuleProvider(callbackProvider)
                .SwitchAutoUpdateSchemeBeforeGetAvailableCommandsOn()
                .RegisterAssemblyForCodeActions(Assembly.GetExecutingAssembly());
        }

        public void Start()
        {
            _runtime.Start();
        }

        public string DesignerApi(NameValueCollection pars, Stream filestream = null)
        {
            return _runtime.DesignerAPI(pars, filestream, true);
        }

        #region Create Runtime

        private WorkflowRuntime CreateRuntimeMongoDB()
        {
            var provider = new OptimaJet.Workflow.MongoDB.MongoDBProvider(new MongoClient(Parameters.DBUrl).GetServer().GetDatabase(Parameters.Database));
            callbackProvider = new WorkflowCallbackProvider(this.Parameters, provider);

            var builder = new WorkflowBuilder<XElement>(callbackProvider, new XmlWorkflowParser(), provider).WithDefaultCache();
            return new WorkflowRuntime(Parameters.RuntimeId)
                .WithBuilder(builder)
                .WithPersistenceProvider(provider);
        }

        private WorkflowRuntime CreateRuntimeRavenDB()
        {
            var provider = new RavenDBProvider(new Raven.Client.Document.DocumentStore()
            {
                Url = Parameters.DBUrl,
                DefaultDatabase = Parameters.Database
            });

            callbackProvider = new WorkflowCallbackProvider(Parameters, provider);

            var builder = new WorkflowBuilder<XElement>(callbackProvider, new XmlWorkflowParser(), provider).WithDefaultCache();
            return new WorkflowRuntime(Parameters.RuntimeId)
                .WithBuilder(builder)
                .WithPersistenceProvider(provider);
        }

        private WorkflowRuntime CreateRuntimePostgreSQL()
        {
            var provider = new OptimaJet.Workflow.PostgreSQL.PostgreSQLProvider(Parameters.ConnectionString);
            callbackProvider = new WorkflowCallbackProvider(Parameters, provider);
            var builder = new WorkflowBuilder<XElement>(callbackProvider, new XmlWorkflowParser(), provider).WithDefaultCache();
            return new WorkflowRuntime(Parameters.RuntimeId)
                .WithBuilder(builder)
                .WithPersistenceProvider(provider);
        }

        private WorkflowRuntime CreateRuntimeMySQL()
        {
            var provider = new OptimaJet.Workflow.MySQL.MySQLProvider(Parameters.ConnectionString);
            callbackProvider = new WorkflowCallbackProvider(Parameters, provider);
            var builder = new WorkflowBuilder<XElement>(callbackProvider, new XmlWorkflowParser(), provider).WithDefaultCache();
            return new WorkflowRuntime(Parameters.RuntimeId)
                .WithBuilder(builder)
                .WithPersistenceProvider(provider);
        }

        private WorkflowRuntime CreateRuntimeOracle()
        {
            var provider = new OracleProvider(Parameters.ConnectionString);
            callbackProvider = new WorkflowCallbackProvider(Parameters, provider);
            var builder = new WorkflowBuilder<XElement>(callbackProvider, new XmlWorkflowParser(), provider).WithDefaultCache();
            return new WorkflowRuntime(Parameters.RuntimeId)
                .WithBuilder(builder)
                .WithPersistenceProvider(provider);
        }

        private WorkflowRuntime CreateRuntimeMSSQL()
        {
            var generator = new OptimaJet.Workflow.DbPersistence.DbXmlWorkflowGenerator(Parameters.ConnectionString);
            callbackProvider = new WorkflowCallbackProvider(Parameters, generator);
            var builder = new WorkflowBuilder<XElement>(callbackProvider,
                new XmlWorkflowParser(),
                new OptimaJet.Workflow.DbPersistence.DbSchemePersistenceProvider(Parameters.ConnectionString)
            ).WithDefaultCache();

            return new WorkflowRuntime(Parameters.RuntimeId)
                .WithBuilder(builder)
                .WithPersistenceProvider(new OptimaJet.Workflow.DbPersistence.DbPersistenceProvider(Parameters.ConnectionString));
        }

        #endregion

        #region Processing

        public async Task<Response> WorkflowApiProcessing(RequestContext ctx)
        {
            object data = string.Empty;
            string error = string.Empty;

            try
            {
                string operation = ctx.Request.HttpParams.QueryString["operation"];
                if (string.IsNullOrWhiteSpace(operation))
                {
                    throw new Exception("Parameter 'operation' is required!");
                }

                Guid processid;
                if (!Guid.TryParse(ctx.Request.HttpParams.QueryString["processid"], out processid))
                {
                    throw new Exception("Parameter 'processid' is required and must be is GUID!");
                }
                var identityid = ctx.Request.HttpParams.QueryString["identityid"];
                var impersonatedIdentityId = ctx.Request.HttpParams.QueryString["impersonatedIdentityId"];
                var value = ctx.Request.HttpParams.QueryString["parameters"];

                var parameters = !string.IsNullOrWhiteSpace(value) ? JsonConvert.DeserializeObject<Dictionary<string, object>>(value) : null;

                CultureInfo culture = CultureInfo.CurrentUICulture;
                if (!string.IsNullOrWhiteSpace(ctx.Request.HttpParams.QueryString["culture"]))
                {
                    culture = new CultureInfo(ctx.Request.HttpParams.QueryString["culture"]);
                }

                switch (operation.ToLower())
                {
                    case "createinstance":
                        var schemacode = ctx.Request.HttpParams.QueryString["schemacode"];
                        if (string.IsNullOrWhiteSpace(schemacode))
                        {
                            throw new Exception("Parameter 'schemacode' is required!");
                        }

                        if (parameters == null)
                            parameters = new Dictionary<string, object>();

                        var initialPrameters = await GetInitialProcessParameters(ctx, schemacode);

                        var createInstanceParams = new CreateInstanceParams(schemacode, processid)
                        {
                            IdentityId = identityid,
                            ImpersonatedIdentityId = impersonatedIdentityId,
                            InitialProcessParameters = initialPrameters,
                            SchemeCreationParameters = parameters
                        };

                        await _runtime.CreateInstanceAsync(createInstanceParams);
                        break;

                    case "getavailablecommands":
                        var availableCommands = await _runtime.GetAvailableCommandsAsync(processid, new List<string>() {identityid}, null, impersonatedIdentityId);
                        data = JsonConvert.SerializeObject(availableCommands);
                        break;

                    case "executecommand":
                        var command = ctx.Request.HttpParams.QueryString["command"];
                        var wfcommands = await _runtime.GetAvailableCommandsAsync(processid, new List<string>() {identityid}, command, impersonatedIdentityId);
                        var wfcommand = wfcommands.FirstOrDefault();
                        if (wfcommand == null)
                            throw new Exception(string.Format("Command {0} is not found", command));

                        FillCommandParameters(ctx,wfcommand);
                        await _runtime.ExecuteCommandAsync(wfcommand, identityid, impersonatedIdentityId);
                        break;

                    case "getavailablestatetoset":
                        var availableStateToSet = await _runtime.GetAvailableStateToSetAsync(processid, culture);
                        data = JsonConvert.SerializeObject(availableStateToSet);
                        break;

                    case "setstate":
                        var state = ctx.Request.HttpParams.QueryString["state"];

                        if (parameters == null)
                            parameters = new Dictionary<string, object>();

                        await _runtime.SetStateAsync(processid, identityid, impersonatedIdentityId, state, parameters);
                        break;

                    case "isexistprocess":
                        var isProcessExists = await _runtime.IsProcessExistsAsync(processid);
                        data = JsonConvert.SerializeObject(isProcessExists);
                        break;
                    default:
                        throw new Exception(string.Format("operation={0} is not suported!", operation));
                }
            }
            catch (Exception ex)
            {
                error = string.Format("{0}{1}",
                    ex.Message, ex.InnerException == null
                        ? string.Empty
                        : string.Format(". InnerException: {0}", ex.InnerException.Message));
            }

            var res = JsonConvert.SerializeObject(new
            {
                data = data,
                success = error.Length == 0,
                error = error
            });
            return new StringResponse(res);
        }

        public Task<Response> DesignerApiProcessing(RequestContext ctx)
        {
            return Task.Run(() => DesignerApiProcessingSync(ctx));
        }

        private Response DesignerApiProcessingSync(RequestContext ctx)
        {
            Response response;
            try
            {
                NameValueCollection pars = new NameValueCollection();
                pars.Add(ctx.Request.HttpParams.QueryString);

                Stream fileStream = null;
                if (ctx.Request.HttpMethod.Equals("POST", StringComparison.InvariantCultureIgnoreCase) && ctx.Request.HttpParams.FormParams != null)
                {
                    pars.Add(ctx.Request.HttpParams.FormParams);
                    fileStream = ctx.Request.HttpParams.FirstFileStream;
                }

                var res = DesignerApi(pars, fileStream);
                if (pars.AllKeys.Contains("operation") && pars["operation"] == "downloadscheme")
                {
                    response = new StringResponse(res, "file/xml", new Dictionary<string, string>()
                    {
                        {"Content-Disposition", "attachment; filename=schema.xml"}
                    });
                }
                else
                {
                    response = new StringResponse(res);
                }
            }
            catch (Exception ex)
            {
                if (this.Parameters.Log != null)
                    this.Parameters.Log(ex.ToString());
                response = new EmptyResponse(404);
            }
            return response;
        }

        #endregion

        public static void RegisterLicenseKey(string key)
        {
            WorkflowRuntime.RegisterLicense(key);
        }

        private async Task<Dictionary<string, object>> GetInitialProcessParameters(RequestContext ctx, string schemeCode)
        {
            var formParameters = ctx.Request.HttpParams.FormParams;
            if (!ctx.Request.HttpMethod.Equals("POST", StringComparison.InvariantCultureIgnoreCase) || formParameters == null || !formParameters.HasKeys())
            {
                return new Dictionary<string, object>();
            }

            var scheme = await Task.Run(() => _runtime.Builder.GetProcessScheme(schemeCode));

            var result = new Dictionary<string, object>();

            foreach (var key in formParameters.AllKeys)
            {
                var parameter = scheme.Parameters.FirstOrDefault(p => p.Name.Equals(key));
                if (parameter == null)
                    continue;
                var value = DeserializeValue(formParameters, key, parameter.Type);
                result.Add(parameter.Name, value);
            }
            return result;
        }

        private void FillCommandParameters(RequestContext ctx, WorkflowCommand command)
        {
            var formParameters = ctx.Request.HttpParams.FormParams;
            command.SetAllParametersToDefault();
            if (!ctx.Request.HttpMethod.Equals("POST", StringComparison.InvariantCultureIgnoreCase) || formParameters == null || !formParameters.HasKeys())
                return;

            foreach (var key in formParameters.AllKeys)
            {
                var parameter = command.Parameters.FirstOrDefault(p => p.ParameterName.Equals(key));
                if (parameter == null)
                    continue;
                var value = DeserializeValue(formParameters, key, parameter.Type);

                parameter.Value = value;
            }
        }

        private static object DeserializeValue(NameValueCollection formParameters, string key, Type type)
        {
            object value = null;
            try
            {
                value = ParametersSerializer.Deserialize(formParameters[key], type);
            }
            catch (Exception)
            {
                if (type == typeof(string))
                {
                    value = formParameters[key];
                }
                else
                {
                    throw;
                }
            }
            return value;
        }
    }
}
