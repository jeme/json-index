using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Security.Cryptography;
using Lucene.Net.QueryParsers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Schema;

namespace DotJEM.Json.Index.Schema
{
    public interface IJSchemaGenerator
    {
        JSchema Generate(JObject json);
        JSchema Update(JSchema schema, JObject json);
    }

    public class JSchemaGenerator : IJSchemaGenerator
    {
        private readonly Uri root;

        public JSchemaGenerator(string uri) 
            : this(new Uri(uri))
        {
        }

        public JSchemaGenerator(Uri root)
        {
            this.root = root;
        }

        public JSchema Generate(JObject json)
        {
            return InternalGenerate(json, "");
        }

        public JSchema Update(JSchema schema, JObject json)
        {
            return InternalUpdate(schema, json, "");
        }

        private JSchema InternalUpdate(JSchema schema, JToken json, JPath path)
        {
            return schema;
        }

        private JSchema InternalGenerate(JObject json, JPath path)
        {
            if (json == null) return null;

            JSchema schema = new JSchema(JsonSchemaType.Object)
            {
                Id = root + "/" + path.ToString("/"), 
                Field = path.ToString(".")
            };

            IDictionary<string, JSchema> properties = new Dictionary<string, JSchema>();
            foreach (JProperty property in json.Properties())
            {
                JPath child = path + property.Name;
                var prop = InternalGenerate(property.Value as JValue, child)
                        ?? InternalGenerate(property.Value as JArray, child)
                        ?? InternalGenerate(property.Value as JObject, child);
                if (prop != null)
                {
                    properties[property.Name] = prop;
                }
            }
            if (properties.Any())
            {
                schema.Properties = properties;
            }

            return schema;
        }

        private JSchema InternalGenerate(JValue json, JPath path)
        {
            if (json == null) return null;

            var schema = new JSchema(json.Type.ToSchemaType());
            schema.Id = root + "/" + path.ToString("/");
            schema.Field = path.ToString(".");
            return schema;
        }

        private JSchema InternalGenerate(JArray json, JPath path)
        {
            if (json == null) return null;

            return new JSchema(JsonSchemaType.Array)
            {
                Id = root + "/"+ path.ToString("/"),
                Field = path.ToString("."),
                Items = json.Aggregate(new JSchema(JsonSchemaType.None), (schema, token) => InternalUpdate(schema, token, path))
            };
        }
    }

    [JsonConverter(typeof(JSchemeConverter))]
    public class JSchema
    {
        public string Id { get; set; }
        public string Field { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }

        public bool Required { get; set; }

        public JsonSchemaType Type { get; set; }
        public JSchema Items { get; set; }
        public IDictionary<string, JSchema> Properties { get; set; }

        public JSchema(JsonSchemaType type)
        {
            Type = type;
            Required = false;
        }
    }

    [JsonConverter(typeof(JSchemeConverter))]
    public class JRootSchema : JSchema
    {
        public string Schema { get; set; }

        public JRootSchema(JsonSchemaType type) : base(type)
        {
            Schema = "kk";
        }
    }

    //[Flags]
    //public enum JsonSchemaType
    //{
    //    None = 0,
    //    String = 1,
    //    Float = 2,
    //    Integer = 4,
    //    Boolean = 8,
    //    Object = 16,
    //    Array = 32,
    //    Null = 64,
    //    Any = Null | Array | Object | Boolean | Integer | Float | String,
    //}


    //{
    //    "type":"object",
    //    "$schema": "http://json-schema.org/draft-03/schema",
    //    "id": "http://jsonschema.net",
    //    "required":false,
    //    "properties":{
    //        "address": {
    //            "type":"object",
    //            "id": "http://jsonschema.net/address",
    //            "required":false,
    //            "properties":{
    //                "city": {
    //                    "type":["string","integer","object"],
    //                    "title": "test",
    //                    "name": "test",
    //                    "description": "test",
    //                    "id": "http://jsonschema.net/address/city",
    //                    "required":false
    //                },
    //                "streetAddress": {
    //                    "type":"string",
    //                    "id": "http://jsonschema.net/address/streetAddress",
    //                    "required":false
    //                }
    //            }
    //        },
    //        "phoneNumber": {
    //            "type":"array",
    //            "id": "http://jsonschema.net/phoneNumber",
    //            "required":false,
    //            "items":
    //                {
    //                    "type":"object",
    //                    "id": "http://jsonschema.net/phoneNumber/0",
    //                    "required":false,
    //                    "properties":{
    //                        "number": {
    //                            "type":"string",
    //                            "id": "http://jsonschema.net/phoneNumber/0/number",
    //                            "required":false
    //                        },
    //                        "type": {
    //                            "type":"string",
    //                            "id": "http://jsonschema.net/phoneNumber/0/type",
    //                            "required":false
    //                        }
    //                    }
    //                }
			

    //        }
    //    }
    //}

}