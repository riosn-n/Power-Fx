﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Interfaces;
using Microsoft.OpenApi.Models;
using Microsoft.PowerFx.Core;
using Microsoft.PowerFx.Core.Binding;
using Microsoft.PowerFx.Core.Binding.BindInfo;
using Microsoft.PowerFx.Core.IR;
using Microsoft.PowerFx.Core.Utils;
using Microsoft.PowerFx.Types;
using static Microsoft.PowerFx.Connectors.Constants;

namespace Microsoft.PowerFx.Connectors
{
    // See definitions for x-ms extensions:
    // https://docs.microsoft.com/en-us/connectors/custom-connectors/openapi-extensions#x-ms-visibility
    public static class OpenApiExtensions
    {
        // Keep these constants all lower case
        public const string ContentType_TextJson = "text/json";
        public const string ContentType_XWwwFormUrlEncoded = "application/x-www-form-urlencoded";
        public const string ContentType_ApplicationJson = "application/json";
        public const string ContentType_ApplicationOctetStream = "application/octet-stream";
        public const string ContentType_TextCsv = "text/csv";
        public const string ContentType_TextPlain = "text/plain";
        public const string ContentType_Any = "*/*";
        public const string ContentType_Multipart = "multipart/form-data";

        private static readonly IReadOnlyList<string> _knownContentTypes = new string[]
        {
            ContentType_ApplicationJson,
            ContentType_XWwwFormUrlEncoded,
            ContentType_TextJson,
            ContentType_Multipart
        };

        public static string GetBasePath(this OpenApiDocument openApiDocument, SupportsConnectorErrors errors) => GetUriElement(openApiDocument, (uri) => uri.PathAndQuery, errors);

        public static string GetScheme(this OpenApiDocument openApiDocument, SupportsConnectorErrors errors) => GetUriElement(openApiDocument, (uri) => uri.Scheme, errors);

        public static string GetAuthority(this OpenApiDocument openApiDocument, SupportsConnectorErrors errors) => GetUriElement(openApiDocument, (uri) => uri.Authority, errors);

        private static string GetUriElement(this OpenApiDocument openApiDocument, Func<Uri, string> getElement, SupportsConnectorErrors errors)
        {
            if (openApiDocument?.Servers == null)
            {
                return null;
            }

            // Exclude unsecure servers
            var servers = openApiDocument.Servers.Where(srv => srv.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)).ToArray();

            // See https://spec.openapis.org/oas/v3.1.0#server-object
            var count = servers.Length;
            switch (count)
            {
                case 0:
                    // None
                    return null;

                case 1:
                    // This is a full URL that will pull in 'basePath' property from connectors.
                    // Extract BasePath back out from this.
                    var fullPath = openApiDocument.Servers[0].Url;
                    var uri = new Uri(fullPath);
                    return getElement(uri);

                default:
                    errors.AddError($"Multiple servers in OpenApiDocument is not supported");
                    return null;
            }
        }

        public static string GetBodyName(this OpenApiRequestBody requestBody)
        {
            return requestBody.Extensions.TryGetValue(XMsBodyName, out IOpenApiExtension value) && value is OpenApiString oas ? oas.Value : "body";
        }

        // Get suggested options values.  Returns null if none.
        internal static DisplayNameProvider GetEnumValues(this ISwaggerParameter openApiParameter)
        {
            // x-ms-enum-values is: array of { value: string, displayName: string}.
            if (openApiParameter.Extensions.TryGetValue(XMsEnumValues, out var enumValues))
            {
                if (enumValues is IList<IOpenApiAny> array)
                {
                    SingleSourceDisplayNameProvider displayNameProvider = new SingleSourceDisplayNameProvider();

                    foreach (var item in array)
                    {
                        string logical = null;
                        string display = null;

                        if (item is IDictionary<string, IOpenApiAny> obj)
                        {
                            if (obj.TryGetValue("value", out IOpenApiAny openApiLogical))
                            {
                                if (openApiLogical is OpenApiString logicalStr)
                                {
                                    logical = logicalStr.Value;
                                }
                                else if (openApiLogical is OpenApiInteger logicalInt)
                                {
                                    logical = logicalInt.Value.ToString(CultureInfo.InvariantCulture);
                                }
                            }

                            if (obj.TryGetValue("displayName", out IOpenApiAny openApiDisplay))
                            {
                                if (openApiDisplay is OpenApiString displayStr)
                                {
                                    display = displayStr.Value;
                                }
                                else if (openApiDisplay is OpenApiInteger displayInt)
                                {
                                    display = displayInt.Value.ToString(CultureInfo.InvariantCulture);
                                }
                            }

                            if (!string.IsNullOrEmpty(logical) && !string.IsNullOrEmpty(display))
                            {
                                displayNameProvider = displayNameProvider.AddField(new DName(logical), new DName(display));
                            }
                        }
                    }

                    return displayNameProvider.LogicalToDisplayPairs.Any() ? displayNameProvider : null;
                }
            }

            return null;
        }

        public static bool IsTrigger(this OpenApiOperation op)
        {
            // https://learn.microsoft.com/en-us/connectors/custom-connectors/openapi-extensions#x-ms-trigger
            // Identifies whether the current operation is a trigger that produces a single event.
            // The absence of this field means this is an action operation.
            return op.Extensions.ContainsKey(XMsTrigger);
        }

        internal static bool TryGetDefaultValue(this ISwaggerSchema schema, FormulaType formulaType, out FormulaValue defaultValue, SupportsConnectorErrors errors)
        {
            if (schema.Type == "array" && formulaType is TableType tableType && schema.Items != null)
            {
                RecordType recordType = tableType.ToRecord();
                bool b = schema.Items.TryGetDefaultValue(recordType, out FormulaValue itemDefaultValue, errors);

                if (!b || itemDefaultValue is BlankValue)
                {
                    defaultValue = FormulaValue.NewTable(recordType);
                    return true;
                }

                if (itemDefaultValue is RecordValue itemDefaultRecordValue)
                {
                    defaultValue = FormulaValue.NewTable(recordType, itemDefaultRecordValue);
                    return true;
                }

                defaultValue = FormulaValue.NewTable(recordType, FormulaValue.NewRecordFromFields(new NamedValue[] { new NamedValue(TableValue.ValueName, itemDefaultValue) }));
                return true;
            }

            if (schema.Type == "object" || schema.Default == null)
            {
                if (formulaType is RecordType recordType2 && schema.Properties != null)
                {
                    List<NamedValue> values = new List<NamedValue>();

                    foreach (NamedFormulaType namedFormulaType in recordType2.GetFieldTypes())
                    {
                        string columnName = namedFormulaType.Name.Value;

                        if (schema.Properties.TryGetValue(columnName, out ISwaggerSchema innerSchema))
                        {
                            if (innerSchema.TryGetDefaultValue(namedFormulaType.Type, out FormulaValue innerDefaultValue, errors))
                            {
                                values.Add(new NamedValue(columnName, innerDefaultValue));
                            }
                            else
                            {
                                values.Add(new NamedValue(columnName, FormulaValue.NewBlank(namedFormulaType.Type)));
                            }
                        }
                    }

                    defaultValue = values.Any(v => v.Value is not BlankValue) ? FormulaValue.NewRecordFromFields(values) : FormulaValue.NewBlank(recordType2);
                    return true;
                }

                if (schema.Default == null)
                {
                    defaultValue = null;
                    return false;
                }
            }

            return TryGetOpenApiValue(schema.Default, formulaType, out defaultValue, errors);
        }

        internal static bool TryGetOpenApiValue(IOpenApiAny openApiAny, FormulaType formulaType, out FormulaValue formulaValue, SupportsConnectorErrors errors, bool allowOpenApiDateTime = false)
        {
            formulaValue = null;

            if (openApiAny == null)
            {
                return false;
            }
            else if (openApiAny is OpenApiString str)
            {
                if (formulaType != null && formulaType is not StringType && formulaType is not RecordType)
                {
                    if (formulaType is BooleanType && bool.TryParse(str.Value, out bool b))
                    {
                        formulaValue = FormulaValue.New(b);
                    }
                    else if (formulaType is DecimalType && decimal.TryParse(str.Value, out decimal d))
                    {
                        formulaValue = FormulaValue.New(d);
                    }
                    else if (formulaType is DateTimeType)
                    {
                        if (DateTime.TryParse(str.Value, out DateTime dt))
                        {
                            formulaValue = FormulaValue.New(dt);
                        }
                        else
                        {
                            errors.AddError($"Unsupported DateTime format: {str.Value}");
                            return false;
                        }
                    }
                    else if (formulaType is NumberType && double.TryParse(str.Value, out double dbl))
                    {
                        formulaValue = FormulaValue.New(dbl);
                    }
                    else if (formulaType is OptionSetValueType osvt && osvt.TryGetValue(new DName(str.Value), out OptionSetValue osv))
                    {
                        formulaValue = osv;
                    }
                }

                formulaValue ??= FormulaValue.New(str.Value);
            }
            else if (openApiAny is OpenApiInteger intVal)
            {
                formulaValue = FormulaValue.New((decimal)intVal.Value);
            }
            else if (openApiAny is OpenApiDouble dbl)
            {
                formulaValue = FormulaValue.New((decimal)dbl.Value);
            }
            else if (openApiAny is OpenApiLong lng)
            {
                formulaValue = FormulaValue.New((decimal)lng.Value);
            }
            else if (openApiAny is OpenApiBoolean b)
            {
                formulaValue = FormulaValue.New(b.Value);
            }
            else if (openApiAny is OpenApiFloat flt)
            {
                formulaValue = FormulaValue.New((decimal)flt.Value);
            }
            else if (openApiAny is OpenApiByte by)
            {
                // OpenApi library uses Convert.FromBase64String
                formulaValue = FormulaValue.New(Convert.ToBase64String(by.Value));
            }
            else if (openApiAny is OpenApiDateTime dt && allowOpenApiDateTime)
            {
                formulaValue = FormulaValue.New(new DateTime(dt.Value.Ticks, DateTimeKind.Utc));
            }
            else if (openApiAny is OpenApiDate dte && allowOpenApiDateTime)
            {
                formulaValue = FormulaValue.NewDateOnly(new DateTime(dte.Value.Ticks, DateTimeKind.Utc));
            }
            else if (openApiAny is OpenApiPassword pw)
            {
                formulaValue = FormulaValue.New(pw.Value);
            }
            else if (openApiAny is OpenApiNull)
            {
                formulaValue = FormulaValue.NewBlank();
            }
            else if (openApiAny is IList<IOpenApiAny> arr)
            {
                List<FormulaValue> lst = new List<FormulaValue>();

                foreach (IOpenApiAny element in arr)
                {
                    FormulaType newType = null;

                    if (formulaType != null)
                    {
                        if (formulaType is TableType tableType)
                        {
                            newType = tableType.ToRecord();
                        }
                    }

                    if (!TryGetOpenApiValue(element, newType, out FormulaValue fv, errors))
                    {
                        formulaValue = null;
                        return false;
                    }

                    lst.Add(fv);
                }

                RecordType recordType = RecordType.Empty().Add(TableValue.ValueName, FormulaType.String);
                IRContext irContext = IRContext.NotInSource(recordType);
                IEnumerable<InMemoryRecordValue> recordValues = lst.Select(item => new InMemoryRecordValue(irContext, new NamedValue[] { new NamedValue(TableValue.ValueName, item) }));

                formulaValue = FormulaValue.NewTable(recordType, recordValues);
            }
            else if (openApiAny is IDictionary<string, IOpenApiAny> o)
            {
                Dictionary<string, FormulaValue> dvParams = new ();

                foreach (KeyValuePair<string, IOpenApiAny> kvp in o)
                {
                    if (TryGetOpenApiValue(kvp.Value, null, out FormulaValue fv, errors))
                    {
                        dvParams[kvp.Key] = fv;
                    }
                }

                formulaValue = FormulaValue.NewRecordFromFields(dvParams.Select(dvp => new NamedValue(dvp.Key, dvp.Value)));
            }
            else
            {
                errors.AddError($"Unknown default value type {openApiAny.GetType().FullName}");
                return false;
            }

            return true;
        }

        internal static bool IsInternal(this IOpenApiExtensible oae) => SwaggerExtensions.New(oae)?.IsInternal() ?? false;

        internal static string GetVisibility(this IOpenApiExtensible oae) => SwaggerExtensions.New(oae)?.GetVisibility();

        internal static string GetEnumName(this IOpenApiExtensible oae) => SwaggerExtensions.New(oae)?.GetEnumName();

        // Internal parameters are not showen to the user.
        // They can have a default value or be special cased by the infrastructure (like "connectionId").
        internal static bool IsInternal(this ISwaggerExtensions schema) => string.Equals(schema.GetVisibility(), "internal", StringComparison.OrdinalIgnoreCase);

        internal static string GetVisibility(this ISwaggerExtensions schema) => schema.Extensions.TryGetValue(XMsVisibility, out IOpenApiExtension openApiExt) && openApiExt is OpenApiString openApiStr ? openApiStr.Value : null;

        internal static string GetEnumName(this ISwaggerExtensions schema) => schema.Extensions.TryGetValue(XMsEnum, out IOpenApiExtension openApiExt) &&
                                                                              openApiExt is SwaggerJsonObject jsonObject &&
                                                                              jsonObject.TryGetValue("name", out IOpenApiAny enumName) &&
                                                                              enumName is OpenApiString enumNameStr
                                                                            ? enumNameStr.Value
                                                                            : null;

        internal static string GetMediaKind(this ISwaggerExtensions schema) => schema.Extensions.TryGetValue(XMsMediaKind, out IOpenApiExtension openApiExt) && openApiExt is OpenApiString openApiStr ? openApiStr.Value : null;

        internal static bool? GetNotificationUrl(this ISwaggerExtensions schema) => schema.Extensions.TryGetValue(XMsNotificationUrl, out IOpenApiExtension openApiExt) && openApiExt is OpenApiBoolean openApiBool ? openApiBool.Value : null;

        internal static string GetAiSensitivity(this ISwaggerExtensions schema) => schema.Extensions.TryGetValue(XMsAiSensitivity, out IOpenApiExtension openApiExt) && openApiExt is OpenApiString openApiStr ? openApiStr.Value : null;

        internal static (bool IsPresent, string Value) GetString(this IDictionary<string, IOpenApiAny> apiObj, string str) => apiObj.TryGetValue(str, out IOpenApiAny openApiAny) && openApiAny is OpenApiString openApiStr ? (true, openApiStr.Value) : (false, null);

        internal static void WhenPresent(this IDictionary<string, IOpenApiAny> apiObj, string propName, Action<string> action)
        {
            var (isPresent, value) = apiObj.GetString(propName);
            if (isPresent)
            {
                action(value);
            }
        }

        internal static void WhenPresent(this IDictionary<string, IOpenApiAny> apiObj, string str, Action<IDictionary<string, IOpenApiAny>> action)
        {
            if (apiObj.TryGetValue(str, out IOpenApiAny openApiAny) && openApiAny is IDictionary<string, IOpenApiAny> openApiObj)
            {
                action(openApiObj);
            }
        }

        internal class ConnectorTypeGetterSettings
        {
            internal readonly ConnectorCompatibility Compatibility;            
            internal Stack<string> Chain = new Stack<string>();
            internal int Level = 0;
            internal readonly SymbolTable OptionSets;

            private readonly string _tableName;

            internal ConnectorTypeGetterSettings(ConnectorCompatibility connectorCompatibility, string tableName, SymbolTable optionSets)
            {
                Compatibility = connectorCompatibility;
                OptionSets = optionSets;                

                _tableName = tableName;
            }

            internal ConnectorTypeGetterSettings Stack(string identifier)
            {
                Level++;
                Chain.Push(identifier);
                return this;
            }

            internal void UnStack()
            {
                Chain.Pop();
                Level--;
            }

            // by default, optionset names will be 'propertyName (tableName)' in CDP case, where propertyName is replaced by x-ms-enum content, when provided
            // in non-CDP case, tableName is null and will only be 'propertyName' (or x-ms-enum content)
            internal string GetOptionSetName(string optionSetNameBase)
            {
                string optionSetName = optionSetNameBase;

                if (!string.IsNullOrEmpty(_tableName))
                {
                    optionSetName += $" ({_tableName})";
                }

                return optionSetName;
            }
        }

        internal static ConnectorType GetConnectorType(this ISwaggerParameter openApiParameter, ConnectorCompatibility compatibility)
        {
            return openApiParameter.GetConnectorType(tableName: null, optionSets: null, compatibility);
        }

        internal static ConnectorType GetConnectorType(this ISwaggerParameter openApiParameter, string tableName, SymbolTable optionSets, ConnectorCompatibility compatibility)
        {
            ConnectorTypeGetterSettings settings = new ConnectorTypeGetterSettings(compatibility, tableName, optionSets);
            ConnectorType connectorType = openApiParameter.GetConnectorType(settings);

            return connectorType;
        }

        // See https://swagger.io/docs/specification/data-models/data-types/
        internal static ConnectorType GetConnectorType(this ISwaggerParameter openApiParameter, ConnectorTypeGetterSettings settings)
        {
            if (openApiParameter == null)
            {
                return new ConnectorType(error: "OpenApiParameter is null, can't determine its schema");
            }

            ISwaggerSchema schema = openApiParameter.Schema;

            if (settings.Level == 30)
            {
                return new ConnectorType(error: "GetConnectorType() excessive recursion");
            }

            // schema.Format is optional and potentially any string
            switch (schema.Type)
            {
                // OpenAPI spec: Format could be <null>, byte, binary, date, date-time, password
                case "string":

                    // We don't want to have OptionSets in connections, we'll only get string/number for now in FormulaType
                    // Anyhow, we'll have schema.Enum content in ConnectorType

                    switch (schema.Format)
                    {
                        case "uri":
                            return new ConnectorType(schema, openApiParameter, FormulaType.String);

                        case "date": // full-date RFC3339
                            return new ConnectorType(schema, openApiParameter, FormulaType.Date);

                        case "date-time": // date-time RFC3339
                        case "date-no-tz":
                            return new ConnectorType(schema, openApiParameter, FormulaType.DateTime);

                        case "byte": // Base64 string
                        case "binary": // octet stream
                            return new ConnectorType(schema, openApiParameter, FormulaType.Blob);
                    }

                    // Try getting enum from 'x-ms-enum-values'
                    DisplayNameProvider optionSetDisplayNameProvider = openApiParameter.GetEnumValues();

                    if (optionSetDisplayNameProvider != null && (settings.Compatibility.IsCDP() || schema.Format == "enum"))
                    {
                        string optionSetName = settings.GetOptionSetName(schema.GetEnumName() ?? openApiParameter.Name);
                        OptionSet optionSet = new OptionSet(optionSetName, optionSetDisplayNameProvider);
                        optionSet = settings.OptionSets.TryAddOptionSet(optionSet);
                        return new ConnectorType(schema, openApiParameter, optionSet.FormulaType);
                    }

                    // Try getting enum from 'enum'
                    if (schema.Enum != null && (settings.Compatibility.IsCDP() || schema.Format == "enum"))
                    {
                        if (schema.Enum.All(e => e is OpenApiString))
                        {
                            string optionSetName = settings.GetOptionSetName(schema.GetEnumName() ?? openApiParameter.Name);
                            OptionSet optionSet = new OptionSet(optionSetName, schema.Enum.Select(e => new DName((e as OpenApiString).Value)).ToDictionary(k => k, e => e).ToImmutableDictionary());
                            optionSet = settings.OptionSets.TryAddOptionSet(optionSet);
                            return new ConnectorType(schema, openApiParameter, optionSet.FormulaType);
                        }
                        else
                        {
                            return new ConnectorType(error: $"Unsupported enum type {schema.Enum.GetType().Name}");
                        }
                    }

                    return new ConnectorType(schema, openApiParameter, FormulaType.String);

                // OpenAPI spec: Format could be float, double, or not specified.
                // we assume not specified implies decimal
                // https://swagger.io/docs/specification/data-models/data-types/
                case "number":
                    switch (schema.Format)
                    {
                        case "float":
                        case "double":
                        case "integer":
                        case "byte":
                        case "number":
                        case "int32":
                            return new ConnectorType(schema, openApiParameter, FormulaType.Decimal);

                        case null:
                        case "decimal":
                        case "currency":
                            return new ConnectorType(schema, openApiParameter, FormulaType.Decimal);

                        default:
                            return new ConnectorType(error: $"Unsupported type of number: {schema.Format}");
                    }

                // For testing only
                case "fxnumber":
                    return new ConnectorType(schema, openApiParameter, FormulaType.Number);

                // Always a boolean (Format not used)
                case "boolean":
                    return new ConnectorType(schema, openApiParameter, FormulaType.Boolean);

                // OpenAPI spec: Format could be <null>, int32, int64
                case "integer":
                    switch (schema.Format)
                    {
                        case null:
                        case "byte":
                        case "integer":
                        case "int32":
                            return new ConnectorType(schema, openApiParameter, FormulaType.Decimal);

                        case "int64":
                        case "uint64":
                        case "unixtime":
                            return new ConnectorType(schema, openApiParameter, FormulaType.Decimal);

                        default:
                            return new ConnectorType(error: $"Unsupported type of integer: {schema.Format}");
                    }

                case "array":
                    if (schema.Items == null)
                    {
                        // Type of items in unknown
                        return new ConnectorType(schema, openApiParameter, ConnectorType.DefaultType);
                    }

                    var itemIdentifier = GetUniqueIdentifier(schema.Items);

                    if (itemIdentifier.StartsWith("R:", StringComparison.Ordinal) && settings.Chain.Contains(itemIdentifier))
                    {
                        // Here, we have a circular reference and default to a table
                        return new ConnectorType(schema, openApiParameter, TableType.Empty());
                    }

                    // Inheritance/Polymorphism - Can't know the exact type
                    // https://github.com/OAI/OpenAPI-Specification/blob/main/versions/2.0.md
                    if (schema.Items.Discriminator != null)
                    {
                        return new ConnectorType(schema, openApiParameter, ConnectorType.DefaultType);
                    }

                    //ConnectorType arrayType = new OpenApiParameter() { Name = "Array", Required = true, Schema = schema.Items, Extensions = schema.Items.Extensions }.GetConnectorType(settings.Stack(itemIdentifier));
                    ConnectorType arrayType = new SwaggerParameter("Array", true, schema.Items, schema.Items.Extensions).GetConnectorType(settings.Stack(itemIdentifier));

                    settings.UnStack();

                    if (arrayType.FormulaType is RecordType connectorRecordType)
                    {
                        return new ConnectorType(schema, openApiParameter, connectorRecordType.ToTable(), arrayType);
                    }
                    else if (arrayType.FormulaType is TableType tableType)
                    {
                        // Array of array
                        TableType newTableType = new TableType(TableType.Empty().Add(TableValue.ValueName, tableType)._type);
                        return new ConnectorType(schema, openApiParameter, newTableType, arrayType);
                    }
                    else if (arrayType.FormulaType is not AggregateType)
                    {
                        // Primitives get marshalled as a SingleColumn table.
                        // Make sure this is consistent with invoker.
                        var recordType3 = RecordType.Empty().Add(TableValue.ValueName, arrayType.FormulaType);
                        return new ConnectorType(schema, openApiParameter, recordType3.ToTable(), arrayType);
                    }
                    else
                    {
                        return new ConnectorType(error: $"Unsupported type of array ({arrayType.FormulaType._type.ToAnonymousString()})");
                    }

                case "object":
                case null: // xml

                    // Dictionary - https://swagger.io/docs/specification/data-models/dictionaries/
                    // Key is always a string, Value is in AdditionalProperties
                    if ((schema.AdditionalProperties != null && schema.AdditionalProperties.Properties.Any()) || schema.Discriminator != null)
                    {
                        return new ConnectorType(schema, openApiParameter, ConnectorType.DefaultType);
                    }
                    else
                    {
                        List<(string, string, FormulaType)> fields = new List<(string, string, FormulaType)>();
                        List<(string, string, FormulaType)> hiddenfields = null;

                        List<ConnectorType> connectorTypes = new List<ConnectorType>();
                        List<ConnectorType> hiddenConnectorTypes = new List<ConnectorType>();

                        foreach (KeyValuePair<string, ISwaggerSchema> kv in schema.Properties)
                        {
                            bool hiddenRequired = false;

                            if (kv.Value.IsInternal())
                            {
                                if (schema.Required.Contains(kv.Key))
                                {
                                    if (kv.Value.Default == null)
                                    {
                                        continue;
                                    }

                                    hiddenRequired = true;
                                }
                                else if (settings.Compatibility.ExcludeInternals())
                                {
                                    continue;
                                }
                            }

                            string propLogicalName = kv.Key;
                            string propDisplayName = kv.Value.Title;

                            if (string.IsNullOrEmpty(propDisplayName))
                            {
                                propDisplayName = kv.Value.GetSummary();
                            }

                            if (string.IsNullOrEmpty(propDisplayName))
                            {
                                propDisplayName = kv.Key;
                            }

                            propDisplayName = GetDisplayName(propDisplayName);

                            string schemaIdentifier = GetUniqueIdentifier(kv.Value);

                            if (schemaIdentifier.StartsWith("R:", StringComparison.Ordinal) && settings.Chain.Contains(schemaIdentifier))
                            {
                                // Here, we have a circular reference and default to a string
                                return new ConnectorType(schema, openApiParameter, FormulaType.String, hiddenfields.ToRecordType());
                            }

                            //ConnectorType propertyType = new OpenApiParameter() { Name = propLogicalName, Required = schema.Required.Contains(propLogicalName), Schema = kv.Value, Extensions = kv.Value.Extensions }.GetConnectorType(settings.Stack(schemaIdentifier));
                            ConnectorType propertyType = new SwaggerParameter(propLogicalName, schema.Required.Contains(propLogicalName), kv.Value, kv.Value.Extensions).GetConnectorType(settings.Stack(schemaIdentifier));

                            settings.UnStack();

                            if (propertyType.HiddenRecordType != null)
                            {
                                hiddenfields ??= new List<(string, string, FormulaType)>();
                                hiddenfields.Add((propLogicalName, propDisplayName, propertyType.HiddenRecordType));
                                hiddenConnectorTypes.Add(propertyType); // Hidden
                            }

                            if (hiddenRequired)
                            {
                                hiddenfields ??= new List<(string, string, FormulaType)>();
                                hiddenfields.Add((propLogicalName, propDisplayName, propertyType.FormulaType));
                                hiddenConnectorTypes.Add(propertyType);
                            }
                            else
                            {
                                fields.Add((propLogicalName, propDisplayName, propertyType.FormulaType));
                                connectorTypes.Add(propertyType);
                            }
                        }

                        return new ConnectorType(schema, openApiParameter, fields.ToRecordType(), hiddenfields.ToRecordType(), connectorTypes.ToArray(), hiddenConnectorTypes.ToArray());
                    }

                case "file":
                    return new ConnectorType(schema, openApiParameter, FormulaType.Blob);

                default:
                    return new ConnectorType(error: $"Unsupported schema type {schema.Type}");
            }
        }

        // If an OptionSet doesn't exist, we add it (and return it)
        // If an identical OptionSet exists (same name & list of options), we return it
        // Otherwise we throw in case of conflict
        internal static OptionSet TryAddOptionSet(this SymbolTable symbolTable, OptionSet optionSet)
        {
            if (optionSet == null)
            {
                throw new ArgumentNullException("optionSet");
            }

            if (symbolTable == null)
            {
                return optionSet;
            }

            string name = optionSet.EntityName;

            // No existing symbols with that name
            if (!((INameResolver)symbolTable).Lookup(new DName(name), out NameLookupInfo info, NameLookupPreferences.None))
            {
                symbolTable.AddOptionSet(optionSet);
                return optionSet;
            }

            // Same optionset already present in table
            if (info.Kind == BindKind.OptionSet && info.Data is OptionSet existingOptionSet && existingOptionSet.Equals(optionSet))
            {
                return existingOptionSet;
            }

            throw new InvalidOperationException($"Optionset name conflict ({name})");
        }

        internal static RecordType ToRecordType(this List<(string logicalName, string displayName, FormulaType type)> fields)
        {
            if (fields == null)
            {
                return null;
            }

            HashSet<string> names = new HashSet<string>(fields.Select(f => f.logicalName), StringComparer.InvariantCulture);
            List<(string logicalName, string displayName, FormulaType type)> newPairs = new List<(string, string, FormulaType)>();

            foreach ((string logicalName, string displayName, FormulaType type) field in fields)
            {
                string logicalName = field.logicalName;
                string displayName = string.IsNullOrWhiteSpace(field.displayName) ? null : field.displayName;
                FormulaType formulaType = field.type;

                int i = 0;

                if (displayName != null && logicalName != displayName)
                {
                    while (names.Contains(displayName))
                    {
                        displayName = $"{field.displayName}_{++i}";
                    }

                    names.Add(displayName);
                }

                newPairs.Add((logicalName, displayName, formulaType));
            }

            RecordType rt = RecordType.Empty();

            foreach ((string logicalName, string displayName, FormulaType type) in newPairs)
            {
                rt = rt.Add(logicalName, type, displayName);
            }

            return rt;
        }

        internal static string GetDisplayName(string name)
        {
            string displayName = name.Replace("{", string.Empty).Replace("}", string.Empty);
            return displayName;
        }

        internal static string GetUniqueIdentifier(this ISwaggerSchema schema)
        {
            return schema.Reference != null ? "R:" + schema.Reference.Id : "T:" + schema.Type;
        }

        public static HttpMethod ToHttpMethod(this OperationType key)
        {
            return key switch
            {
                OperationType.Get => HttpMethod.Get,
                OperationType.Put => HttpMethod.Put,
                OperationType.Post => HttpMethod.Post,
                OperationType.Delete => HttpMethod.Delete,
                OperationType.Options => HttpMethod.Options,
                OperationType.Head => HttpMethod.Head,
                OperationType.Trace => HttpMethod.Trace,
                _ => new HttpMethod(key.ToString())
            };
        }

        public static FormulaType GetReturnType(this OpenApiOperation openApiOperation, ConnectorCompatibility compatibility)
        {            
            ConnectorType connectorType = openApiOperation.GetConnectorReturnType(compatibility);
            FormulaType ft = connectorType.HasErrors ? ConnectorType.DefaultType : connectorType?.FormulaType ?? new BlankType();
            return ft;
        }

        public static bool GetRequiresUserConfirmation(this OpenApiOperation op)
        {
            return op.Extensions.TryGetValue(XMsRequireUserConfirmation, out IOpenApiExtension openExt) && openExt is OpenApiBoolean b && b.Value;
        }

        internal static ConnectorType GetConnectorReturnType(this OpenApiOperation openApiOperation, ConnectorCompatibility compatibility)
        {
            OpenApiResponses responses = openApiOperation.Responses;
            OpenApiResponse response = responses.Where(kvp => kvp.Key?.Length == 3 && kvp.Key.StartsWith("2", StringComparison.Ordinal)).OrderBy(kvp => kvp.Key).FirstOrDefault().Value;

            if (response == null)
            {
                // If no 200, but "default", use that.
                if (!responses.TryGetValue("default", out response))
                {
                    // If no default, use the first one we find
                    response = responses.FirstOrDefault().Value;
                }
            }

            if (response == null)
            {
                // Returns UntypedObject by default, without error
                return new ConnectorType(null);
            }

            if (response.Content.Count == 0)
            {
                OpenApiSchema schema = new OpenApiSchema() { Type = "string", Format = "no_format" };
                return new SwaggerParameter("response", true, new SwaggerSchema("string", "no_format"), response.Extensions).GetConnectorType(compatibility);
            }

            // Responses is a list by content-type. Find "application/json"
            // Headers are case insensitive.
            foreach (KeyValuePair<string, OpenApiMediaType> contentKvp in response.Content)
            {
                string mediaType = contentKvp.Key.Split(';')[0];
                OpenApiMediaType openApiMediaType = contentKvp.Value;

                if (string.Equals(mediaType, ContentType_ApplicationJson, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(mediaType, ContentType_TextPlain, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(mediaType, ContentType_Any, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(mediaType, ContentType_ApplicationOctetStream, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(mediaType, ContentType_TextCsv, StringComparison.OrdinalIgnoreCase))
                {
                    if (openApiMediaType.Schema == null)
                    {
                        // Treat as void.
                        return new SwaggerParameter("response", true, new SwaggerSchema("string", "no_format"), response.Extensions).GetConnectorType(compatibility);
                    }

                    return new SwaggerParameter("response", true, SwaggerSchema.New(openApiMediaType.Schema), openApiMediaType.Schema.Extensions).GetConnectorType(compatibility);
                }
            }

            return new ConnectorType(error: $"Unsupported return type - found {string.Join(", ", response.Content.Select(kv4 => kv4.Key))}");
        }

        /// <summary>
        /// Identifies which ContentType and Schema to use.
        /// </summary>
        /// <param name="content"></param>
        /// <returns>RequestBody content dictionary of possible content types and associated schemas.</returns>
        /// <exception cref="NotImplementedException">When we cannot determine the content type to use.</exception>
        public static (string ContentType, OpenApiMediaType MediaType) GetContentTypeAndSchema(this IDictionary<string, OpenApiMediaType> content)
        {
            Dictionary<string, OpenApiMediaType> list = new ();

            foreach (var ct in _knownContentTypes)
            {
                if (content.TryGetValue(ct, out var mediaType))
                {
                    if (ct == ContentType_TextJson && mediaType.Schema.Properties.Any())
                    {
                        continue;
                    }

                    if (ct != ContentType_ApplicationJson)
                    {
                        return (ct, mediaType);
                    }

                    // application/json
                    if (mediaType.Schema.Properties.Any() || mediaType.Schema.Type == "object")
                    {
                        return (ct, mediaType);
                    }

                    if (mediaType.Schema.Type == "string")
                    {
                        return (ContentType_TextPlain, mediaType);
                    }
                }
            }

            // Cannot determine Content-Type
            return (null, null);
        }

        public static Visibility ToVisibility(this string visibility)
        {
            return string.IsNullOrEmpty(visibility)
                ? Visibility.None
                : Enum.TryParse(visibility, true, out Visibility vis)
                ? vis
                : Visibility.Unknown;
        }

        public static AiSensitivity ToAiSensitivity(this string aiSensitivity)
        {
            return string.IsNullOrEmpty(aiSensitivity)
                ? AiSensitivity.None
                : Enum.TryParse(aiSensitivity, true, out AiSensitivity ais)
                ? ais
                : AiSensitivity.Unknown;
        }

        public static MediaKind ToMediaKind(this string mediaKind)
        {
            return string.IsNullOrEmpty(mediaKind)
                ? MediaKind.File
                : Enum.TryParse(mediaKind, true, out MediaKind mk)
                ? mk
                : MediaKind.Unknown;
        }

        internal static string PageLink(this OpenApiOperation op)
            => op.Extensions.TryGetValue(XMsPageable, out IOpenApiExtension ext) &&
               ext is IDictionary<string, IOpenApiAny> oao &&
               oao.Any() &&
               oao.First().Key == "nextLinkName" &&
               oao.First().Value is OpenApiString oas
            ? oas.Value
            : null;

        internal static string GetSummary(this ISwaggerExtensions param)
        {
            // https://learn.microsoft.com/en-us/connectors/custom-connectors/openapi-extensions
            return param.Extensions != null && param.Extensions.TryGetValue(XMsSummary, out IOpenApiExtension ext) && ext is OpenApiString apiStr ? apiStr.Value : null;
        }

        internal static bool GetExplicitInput(this ISwaggerExtensions param)
        {
            return param.Extensions != null && param.Extensions.TryGetValue(XMsExplicitInput, out IOpenApiExtension ext) && ext is OpenApiBoolean apiBool && apiBool.Value;
        }

        internal static ConnectorKeyType GetKeyType(this ISwaggerExtensions param)
        {
            if (param.Extensions != null && param.Extensions.TryGetValue(XMsKeyType, out IOpenApiExtension ext) && ext is OpenApiString apiStr && apiStr != null && !string.IsNullOrEmpty(apiStr.Value))
            {
                if (Enum.TryParse(apiStr.Value, true, out ConnectorKeyType ckt))
                {
                    return ckt;
                }
            }

            return ConnectorKeyType.Undefined;
        }

        internal static double GetKeyOrder(this ISwaggerExtensions param)
        {
            if (param.Extensions != null && param.Extensions.TryGetValue(XMsKeyOrder, out IOpenApiExtension ext) && ext is OpenApiDouble dbl)
            {
                return dbl.Value;
            }

            return 0.0;
        }

        internal static ConnectorPermission GetPermission(this ISwaggerExtensions param)
        {
            if (param.Extensions != null && param.Extensions.TryGetValue(XMsPermission, out IOpenApiExtension ext) && ext is OpenApiString apiStr && apiStr != null && !string.IsNullOrEmpty(apiStr.Value))
            {
                if (apiStr.Value.Equals("read-only", StringComparison.OrdinalIgnoreCase))
                {
                    return ConnectorPermission.PermissionReadOnly;
                }
                else if (apiStr.Value.Equals("read-write", StringComparison.OrdinalIgnoreCase))
                {
                    return ConnectorPermission.PermissionReadWrite;
                }
            }

            return ConnectorPermission.Undefined;
        }

        internal static ServiceCapabilities GetTableCapabilities(this ISwaggerExtensions schema)
        {
            if (schema.Extensions != null && schema.Extensions.TryGetValue(XMsCapabilities, out IOpenApiExtension ext))
            {
                return ServiceCapabilities.ParseTableCapabilities(ext as IDictionary<string, IOpenApiAny>);
            }

            return null;
        }

        internal static ColumnCapabilities GetColumnCapabilities(this ISwaggerExtensions schema)
        {
            if (schema.Extensions != null && schema.Extensions.TryGetValue(XMsCapabilities, out IOpenApiExtension ext))
            {
                return ColumnCapabilities.ParseColumnCapabilities(ext as IDictionary<string, IOpenApiAny>);
            }

            return null;
        }

        internal static Dictionary<string, Relationship> GetRelationships(this ISwaggerExtensions schema)
        {
            if (schema.Extensions != null && schema.Extensions.TryGetValue(XMsRelationships, out IOpenApiExtension ext))
            {
                return Relationship.ParseRelationships(ext as IDictionary<string, IOpenApiAny>);
            }

            return null;
        }

        // Get string content of x-ms-url-encoding parameter extension
        // Values can be "double" or "single" - https://learn.microsoft.com/en-us/connectors/custom-connectors/openapi-extensions#x-ms-url-encoding
        internal static bool GetDoubleEncoding(this IOpenApiExtensible param)
        {
            return param.Extensions != null && param.Extensions.TryGetValue(XMsUrlEncoding, out IOpenApiExtension ext) && ext is OpenApiString apiStr && apiStr.Value.Equals("double", StringComparison.OrdinalIgnoreCase);
        }

        internal static ConnectorDynamicValue GetDynamicValue(this ISwaggerExtensions param)
        {
            // https://learn.microsoft.com/en-us/connectors/custom-connectors/openapi-extensions#use-dynamic-values
            if (param?.Extensions != null && param.Extensions.TryGetValue(XMsDynamicValues, out IOpenApiExtension ext) && ext is IDictionary<string, IOpenApiAny> apiObj)
            {
                // Parameters is required in the spec but there are examples where it's not specified and we'll support this condition with an empty list
                IDictionary<string, IOpenApiAny> op_prms = apiObj.TryGetValue("parameters", out IOpenApiAny openApiAny) && openApiAny is IDictionary<string, IOpenApiAny> apiString ? apiString : null;
                ConnectorDynamicValue cdv = new (op_prms);

                // Mandatory operationId for connectors, except when capibility or builtInOperation are defined
                apiObj.WhenPresent("operationId", (opId) => cdv.OperationId = OpenApiHelperFunctions.NormalizeOperationId(opId));
                apiObj.WhenPresent("value-title", (opValTitle) => cdv.ValueTitle = opValTitle);
                apiObj.WhenPresent("value-path", (opValPath) => cdv.ValuePath = opValPath);
                apiObj.WhenPresent("value-collection", (opValCollection) => cdv.ValueCollection = opValCollection);

                // we don't support BuiltInOperations or capabilities right now
                // return null to indicate that the call to get suggestions is not needed for this parameter
                apiObj.WhenPresent("capability", (string op_capStr) => cdv = null);
                apiObj.WhenPresent("builtInOperation", (string op_bioStr) => cdv = null);

                return cdv;
            }

            return null;
        }

        internal static ConnectorDynamicList GetDynamicList(this ISwaggerExtensions param)
        {
            // https://learn.microsoft.com/en-us/connectors/custom-connectors/openapi-extensions#use-dynamic-values
            if (param?.Extensions != null && param.Extensions.TryGetValue(XMsDynamicList, out IOpenApiExtension ext) && ext is IDictionary<string, IOpenApiAny> apiObj)
            {
                // Mandatory operationId for connectors
                if (apiObj.TryGetValue("operationId", out IOpenApiAny op_id) && op_id is OpenApiString opId)
                {
                    // Parameters is required in the spec but there are examples where it's not specified and we'll support this condition with an empty list
                    IDictionary<string, IOpenApiAny> op_prms = apiObj.TryGetValue("parameters", out IOpenApiAny openApiAny) && openApiAny is IDictionary<string, IOpenApiAny> apiString ? apiString : null;
                    ConnectorDynamicList cdl = new (op_prms)
                    {
                        OperationId = OpenApiHelperFunctions.NormalizeOperationId(opId.Value),
                    };

                    if (apiObj.TryGetValue("itemTitlePath", out IOpenApiAny op_valtitle) && op_valtitle is OpenApiString opValTitle)
                    {
                        cdl.ItemTitlePath = opValTitle.Value;
                    }

                    if (apiObj.TryGetValue("itemsPath", out IOpenApiAny op_valpath) && op_valpath is OpenApiString opValPath)
                    {
                        cdl.ItemPath = opValPath.Value;
                    }

                    if (apiObj.TryGetValue("itemValuePath", out IOpenApiAny op_valcoll) && op_valcoll is OpenApiString opValCollection)
                    {
                        cdl.ItemValuePath = opValCollection.Value;
                    }

                    return cdl;
                }
                else
                {
                    return new ConnectorDynamicList($"Missing mandatory parameters operationId and parameters in {XMsDynamicList} extension");
                }
            }

            return null;
        }

        internal static Dictionary<string, IConnectorExtensionValue> GetParameterMap(this IDictionary<string, IOpenApiAny> opPrms, SupportsConnectorErrors errors, bool forceString = false)
        {
            Dictionary<string, IConnectorExtensionValue> dvParams = new ();

            if (opPrms == null)
            {
                return dvParams;
            }

            foreach (KeyValuePair<string, IOpenApiAny> prm in opPrms)
            {
                if (!TryGetOpenApiValue(prm.Value, null, out FormulaValue fv, errors, forceString))
                {
                    errors.AddError($"Unsupported param with OpenApi type {prm.Value.GetType().FullName}, key = {prm.Key}");
                    continue;
                }

                if (prm.Value is OpenApiDateTime oadt && fv is DateTimeValue dtv)
                {
                    // https://github.com/microsoft/OpenAPI.NET/issues/533
                    // https://github.com/microsoft/Power-Fx/pull/1987 - https://github.com/microsoft/Power-Fx/issues/1982
                    // api-version, x-ms-api-version, X-GitHub-Api-Version...
                    if (forceString && prm.Key.EndsWith("api-version", StringComparison.OrdinalIgnoreCase))
                    {
                        fv = FormulaValue.New(dtv.GetConvertedValue(TimeZoneInfo.Utc).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        // A string like "2022-11-18" is interpreted as a DateTimeOffset
                        // https://github.com/microsoft/OpenAPI.NET/issues/533
                        errors.AddError($"Unsupported OpenApiDateTime {oadt.Value}");
                        continue;
                    }
                }

                if (prm.Value is OpenApiDate oad && fv is DateValue dv)
                {
                    // https://github.com/microsoft/OpenAPI.NET/issues/533
                    // https://github.com/microsoft/Power-Fx/pull/1987 - https://github.com/microsoft/Power-Fx/issues/1982
                    // api-version, x-ms-api-version, X-GitHub-Api-Version...
                    if (forceString && prm.Key.EndsWith("api-version", StringComparison.OrdinalIgnoreCase))
                    {
                        fv = FormulaValue.New(dv.GetConvertedValue(TimeZoneInfo.Utc).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        // A string like "2022-11-18" is interpreted as a DateTimeOffset
                        // https://github.com/microsoft/OpenAPI.NET/issues/533
                        errors.AddError($"Unsupported OpenApiDateTime {oad.Value}");
                        continue;
                    }
                }

                if (fv is not RecordValue rv)
                {
                    dvParams.Add(prm.Key, new StaticConnectorExtensionValue() { Value = fv });
                }
                else
                {
                    FormulaValue staticValue = rv.GetField("value");

                    if (staticValue is not BlankValue)
                    {
                        dvParams.Add(prm.Key, new StaticConnectorExtensionValue() { Value = staticValue });
                        continue;
                    }

                    FormulaValue dynamicValue = rv.GetField("parameter");

                    if (dynamicValue is BlankValue)
                    {
                        dynamicValue = rv.GetField("parameterReference");
                    }

                    if (dynamicValue is StringValue dynamicStringValue)
                    {
                        dvParams.Add(prm.Key, new DynamicConnectorExtensionValue() { Reference = dynamicStringValue.Value });
                    }
                    else
                    {
                        errors.AddError($"Invalid dynamic value type for {prm.Value.GetType().FullName}, key = {prm.Key}");
                    }
                }
            }

            return dvParams;
        }

        internal static ConnectorDynamicSchema GetDynamicSchema(this ISwaggerExtensions param)
        {
            // https://learn.microsoft.com/en-us/connectors/custom-connectors/openapi-extensions#use-dynamic-values
            if (param?.Extensions != null && param.Extensions.TryGetValue(XMsDynamicSchema, out IOpenApiExtension ext) && ext is IDictionary<string, IOpenApiAny> apiObj)
            {
                // Mandatory operationId for connectors
                if (apiObj.TryGetValue("operationId", out IOpenApiAny op_id) && op_id is OpenApiString opId)
                {
                    // Parameters is required in the spec but there are examples where it's not specified and we'll support this condition with an empty list
                    IDictionary<string, IOpenApiAny> op_prms = apiObj.TryGetValue("parameters", out IOpenApiAny openApiAny) && openApiAny is IDictionary<string, IOpenApiAny> apiString ? apiString : null;

                    ConnectorDynamicSchema cds = new (op_prms)
                    {
                        OperationId = OpenApiHelperFunctions.NormalizeOperationId(opId.Value),
                    };

                    if (apiObj.TryGetValue("value-path", out IOpenApiAny op_valpath) && op_valpath is OpenApiString opValPath)
                    {
                        cds.ValuePath = opValPath.Value;
                    }

                    return cds;
                }
                else
                {
                    return new ConnectorDynamicSchema(error: $"Missing mandatory parameters operationId and parameters in {XMsDynamicSchema} extension");
                }
            }

            return null;
        }

        internal static ConnectorDynamicProperty GetDynamicProperty(this ISwaggerExtensions param)
        {
            // https://learn.microsoft.com/en-us/connectors/custom-connectors/openapi-extensions#use-dynamic-values
            if (param?.Extensions != null && param.Extensions.TryGetValue(XMsDynamicProperties, out IOpenApiExtension ext) && ext is IDictionary<string, IOpenApiAny> apiObj)
            {
                // Mandatory operationId for connectors
                if (apiObj.TryGetValue("operationId", out IOpenApiAny op_id) && op_id is OpenApiString opId)
                {
                    // Parameters is required in the spec but there are examples where it's not specified and we'll support this condition with an empty list
                    IDictionary<string, IOpenApiAny> op_prms = apiObj.TryGetValue("parameters", out IOpenApiAny openApiAny) && openApiAny is IDictionary<string, IOpenApiAny> apiString ? apiString : null;

                    ConnectorDynamicProperty cdp = new (op_prms)
                    {
                        OperationId = OpenApiHelperFunctions.NormalizeOperationId(opId.Value),
                    };

                    if (apiObj.TryGetValue("itemValuePath", out IOpenApiAny op_valpath) && op_valpath is OpenApiString opValPath)
                    {
                        cdp.ItemValuePath = opValPath.Value;
                    }

                    return cdp;
                }
                else
                {
                    return new ConnectorDynamicProperty(error: $"Missing mandatory parameters operationId and parameters in {XMsDynamicProperties} extension");
                }
            }

            return null;
        }

        internal static bool ExcludeInternals(this ConnectorCompatibility compatibility) => compatibility == ConnectorCompatibility.SwaggerCompatibility ||
                                                                                            compatibility == ConnectorCompatibility.CdpCompatibility;

        internal static bool IsPowerAppsCompliant(this ConnectorCompatibility compatibility) => compatibility == ConnectorCompatibility.PowerAppsCompatibility;

        internal static bool IncludeUntypedObjects(this ConnectorCompatibility compatibility) => compatibility == ConnectorCompatibility.SwaggerCompatibility ||
                                                                                                 compatibility == ConnectorCompatibility.CdpCompatibility;

        internal static bool IsCDP(this ConnectorCompatibility compatibility) => compatibility == ConnectorCompatibility.CdpCompatibility;
    }
}
