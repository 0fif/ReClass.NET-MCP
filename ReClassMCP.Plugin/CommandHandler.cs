using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using ReClassNET.Memory;
using ReClassNET.Nodes;
using ReClassNET.Plugins;

namespace ReClassMCP
{
    public class CommandHandler
    {
        private static readonly PropertyInfo ClassUuidProperty = typeof(ClassNode).GetProperty("Uuid");

        private readonly IPluginHost host;

        public CommandHandler(IPluginHost host)
        {
            this.host = host;
        }

        public JObject Execute(string command, JObject args)
        {
            try
            {
                switch (command.ToLowerInvariant())
                {
                    case "ping":
                        return Success(new JObject { ["message"] = "pong" });

                    case "get_status":
                        return GetStatus();

                    case "read_memory":
                        return ReadMemory(args);

                    case "write_memory":
                        return WriteMemory(args);

                    case "get_classes":
                        return GetClasses();

                    case "get_class":
                        return GetClass(args);

                    case "get_nodes":
                        return GetNodes(args);

                    case "get_modules":
                        return GetModules();

                    case "get_sections":
                        return GetSections();

                    case "parse_address":
                        return ParseAddress(args);

                    case "create_class":
                        return CreateClass(args);

                    case "add_node":
                        return AddNode(args);

                    case "add_node_at_offset":
                        return AddNodeAtOffset(args);

                    case "add_class_instance_at_offset":
                        return AddClassInstanceAtOffset(args);

                    case "rename_node":
                        return RenameNode(args);

                    case "set_comment":
                        return SetComment(args);

                    case "change_node_type":
                        return ChangeNodeType(args);

                    case "get_process_info":
                        return GetProcessInfo();

                    default:
                        return Error($"Unknown command: {command}");
                }
            }
            catch (Exception ex)
            {
                return Error(ex.Message);
            }
        }

        private JObject Success(JObject data = null)
        {
            var result = new JObject { ["success"] = true };
            if (data != null)
            {
                foreach (var prop in data.Properties())
                {
                    result[prop.Name] = prop.Value;
                }
            }
            return result;
        }

        private JObject Error(string message)
        {
            return new JObject
            {
                ["success"] = false,
                ["error"] = message
            };
        }

        private JObject GetStatus()
        {
            var isAttached = host.Process?.IsValid ?? false;
            var processName = host.Process?.UnderlayingProcess?.Name ?? "";
            var processId = host.Process?.UnderlayingProcess?.Id.ToInt64() ?? 0;

            return Success(new JObject
            {
                ["attached"] = isAttached,
                ["process_name"] = processName,
                ["process_id"] = processId
            });
        }

        private JObject ReadMemory(JObject args)
        {
            if (!host.Process.IsValid)
                return Error("No process attached");

            var addressStr = args["address"]?.ToString();
            var sizeStr = args["size"]?.ToString();

            if (string.IsNullOrEmpty(addressStr))
                return Error("Missing 'address' parameter");

            if (string.IsNullOrEmpty(sizeStr) || !int.TryParse(sizeStr, out var size))
                return Error("Missing or invalid 'size' parameter");

            if (size <= 0 || size > 0x10000)
                return Error("Size must be between 1 and 65536 bytes");

            IntPtr address;
            if (addressStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                address = (IntPtr)Convert.ToInt64(addressStr.Substring(2), 16);
            }
            else if (long.TryParse(addressStr, out var dec))
            {
                address = (IntPtr)dec;
            }
            else
            {
                // Try parsing as formula/expression
                address = ParseAddressFormula(addressStr);
            }

            var buffer = host.Process.ReadRemoteMemory(address, size);
            if (buffer == null)
                return Error("Failed to read memory");

            return Success(new JObject
            {
                ["address"] = $"0x{address.ToInt64():X}",
                ["size"] = size,
                ["data"] = BitConverter.ToString(buffer).Replace("-", "")
            });
        }

        private JObject WriteMemory(JObject args)
        {
            if (!host.Process.IsValid)
                return Error("No process attached");

            var addressStr = args["address"]?.ToString();
            var dataStr = args["data"]?.ToString();

            if (string.IsNullOrEmpty(addressStr))
                return Error("Missing 'address' parameter");

            if (string.IsNullOrEmpty(dataStr))
                return Error("Missing 'data' parameter");

            IntPtr address;
            if (addressStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                address = (IntPtr)Convert.ToInt64(addressStr.Substring(2), 16);
            }
            else
            {
                address = (IntPtr)Convert.ToInt64(addressStr);
            }

            // Parse hex string to bytes
            var data = new byte[dataStr.Length / 2];
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = Convert.ToByte(dataStr.Substring(i * 2, 2), 16);
            }

            var success = host.Process.WriteRemoteMemory(address, data);
            return success ? Success() : Error("Failed to write memory");
        }

        private JObject GetClasses()
        {
            var project = GetCurrentProject();
            if (project == null)
                return Error("No project loaded");

            var classes = new JArray();
            foreach (var cls in project.Classes)
            {
                classes.Add(new JObject
                {
                    ["uuid"] = GetClassUuid(cls),
                    ["name"] = cls.Name,
                    ["address"] = cls.AddressFormula,
                    ["size"] = cls.MemorySize,
                    ["node_count"] = cls.Nodes.Count,
                    ["comment"] = cls.Comment ?? ""
                });
            }

            return Success(new JObject { ["classes"] = classes });
        }

        private JObject GetClass(JObject args)
        {
            var project = GetCurrentProject();
            if (project == null)
                return Error("No project loaded");

            var identifier = args["id"]?.ToString() ?? args["name"]?.ToString();
            if (string.IsNullOrEmpty(identifier))
                return Error("Missing 'id' or 'name' parameter");

            var classNode = FindClass(project.Classes, identifier);

            if (classNode == null)
                return Error($"Class not found: {identifier}");

            return Success(new JObject
            {
                ["uuid"] = GetClassUuid(classNode),
                ["name"] = classNode.Name,
                ["address"] = classNode.AddressFormula,
                ["size"] = classNode.MemorySize,
                ["comment"] = classNode.Comment ?? "",
                ["nodes"] = SerializeNodes(classNode.Nodes)
            });
        }

        private JObject GetNodes(JObject args)
        {
            var project = GetCurrentProject();
            if (project == null)
                return Error("No project loaded");

            var classId = args["class_id"]?.ToString() ?? args["class_name"]?.ToString();
            if (string.IsNullOrEmpty(classId))
                return Error("Missing 'class_id' or 'class_name' parameter");

            var classNode = FindClass(project.Classes, classId);

            if (classNode == null)
                return Error($"Class not found: {classId}");

            var options = NodeQueryOptions.FromArgs(args);
            if (options.Error != null)
                return Error(options.Error);

            var filteredNodes = FilterNodes(classNode.Nodes, options);
            var totalMatchingNodes = filteredNodes.Count;
            var pagedNodes = filteredNodes
                .Skip(options.StartIndex)
                .Take(options.Limit)
                .ToList();

            var response = new JObject
            {
                ["nodes"] = SerializeNodes(pagedNodes),
                ["total_count"] = classNode.Nodes.Count,
                ["matching_count"] = totalMatchingNodes,
                ["returned_count"] = pagedNodes.Count,
                ["start_index"] = options.StartIndex,
                ["limit"] = options.Limit,
                ["has_more"] = options.StartIndex + pagedNodes.Count < totalMatchingNodes
            };

            if (options.StartIndex + pagedNodes.Count < totalMatchingNodes)
            {
                response["next_start_index"] = options.StartIndex + pagedNodes.Count;
            }

            return Success(response);
        }

        private List<BaseNode> FilterNodes(IReadOnlyList<BaseNode> nodes, NodeQueryOptions options)
        {
            return nodes
                .Where(n => options.IncludePadding || !(n is BaseHexNode))
                .Where(n => options.RangeEnd == null || n.Offset < options.RangeEnd.Value)
                .Where(n => options.Offset == null || n.Offset + n.MemorySize > options.Offset.Value)
                .ToList();
        }

        private JArray SerializeNodes(IReadOnlyList<BaseNode> nodes)
        {
            var result = new JArray();
            foreach (var node in nodes)
            {
                var nodeObj = new JObject
                {
                    ["type"] = node.GetType().Name,
                    ["name"] = node.Name,
                    ["offset"] = node.Offset,
                    ["size"] = node.MemorySize,
                    ["comment"] = node.Comment ?? ""
                };

                // Add type-specific information
                if (node is BaseContainerNode container)
                {
                    nodeObj["children"] = SerializeNodes(container.Nodes);
                }
                else if (node is BaseWrapperNode wrapper && wrapper.InnerNode != null)
                {
                    nodeObj["inner_type"] = wrapper.InnerNode.GetType().Name;
                    if (wrapper.ResolveMostInnerNode() is ClassNode innerClass)
                    {
                        nodeObj["inner_class"] = innerClass.Name;
                        nodeObj["inner_uuid"] = GetClassUuid(innerClass);
                    }
                }

                result.Add(nodeObj);
            }
            return result;
        }

        private JObject GetModules()
        {
            if (!host.Process.IsValid)
                return Error("No process attached");

            var modules = new JArray();
            foreach (var module in host.Process.Modules)
            {
                modules.Add(new JObject
                {
                    ["name"] = module.Name,
                    ["path"] = module.Path,
                    ["start"] = $"0x{module.Start.ToInt64():X}",
                    ["end"] = $"0x{module.End.ToInt64():X}",
                    ["size"] = module.Size.ToInt64()
                });
            }

            return Success(new JObject { ["modules"] = modules });
        }

        private JObject GetSections()
        {
            if (!host.Process.IsValid)
                return Error("No process attached");

            var sections = new JArray();
            foreach (var section in host.Process.Sections)
            {
                sections.Add(new JObject
                {
                    ["name"] = section.Name,
                    ["category"] = section.Category.ToString(),
                    ["protection"] = section.Protection.ToString(),
                    ["type"] = section.Type.ToString(),
                    ["start"] = $"0x{section.Start.ToInt64():X}",
                    ["end"] = $"0x{section.End.ToInt64():X}",
                    ["size"] = section.Size.ToInt64(),
                    ["module"] = section.ModuleName ?? ""
                });
            }

            return Success(new JObject { ["sections"] = sections });
        }

        private JObject ParseAddress(JObject args)
        {
            var formula = args["formula"]?.ToString();
            if (string.IsNullOrEmpty(formula))
                return Error("Missing 'formula' parameter");

            try
            {
                var address = ParseAddressFormula(formula);
                return Success(new JObject
                {
                    ["address"] = $"0x{address.ToInt64():X}",
                    ["decimal"] = address.ToInt64()
                });
            }
            catch (Exception ex)
            {
                return Error($"Failed to parse address: {ex.Message}");
            }
        }

        private IntPtr ParseAddressFormula(string formula)
        {
            // Simple hex parsing
            if (formula.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return (IntPtr)Convert.ToInt64(formula.Substring(2), 16);
            }

            // Try as module+offset (e.g., "game.exe+0x1234")
            if (formula.Contains("+"))
            {
                var parts = formula.Split(new[] { '+' }, 2);
                var moduleName = parts[0].Trim();
                var offsetStr = parts[1].Trim();

                var module = host.Process.Modules.FirstOrDefault(m =>
                    m.Name.Equals(moduleName, StringComparison.OrdinalIgnoreCase));

                if (module != null)
                {
                    long offset;
                    if (offsetStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    {
                        offset = Convert.ToInt64(offsetStr.Substring(2), 16);
                    }
                    else
                    {
                        offset = Convert.ToInt64(offsetStr);
                    }

                    return (IntPtr)(module.Start.ToInt64() + offset);
                }
            }

            // Try as decimal
            if (long.TryParse(formula, out var dec))
            {
                return (IntPtr)dec;
            }

            throw new ArgumentException($"Unable to parse address formula: {formula}");
        }

        private JObject CreateClass(JObject args)
        {
            var name = args["name"]?.ToString();
            var address = args["address"]?.ToString();

            if (string.IsNullOrEmpty(name))
                return Error("Missing 'name' parameter");

            ClassNode classNode = null;

            InvokeOnMainThread(() =>
            {
                // ClassNode.Create() triggers ClassCreated event which auto-adds to project
                classNode = ClassNode.Create();
                classNode.Name = name;

                if (!string.IsNullOrEmpty(address))
                {
                    classNode.AddressFormula = address;
                }
            });

            if (classNode == null)
                return Error("Failed to create class");

            return Success(new JObject
            {
                ["uuid"] = GetClassUuid(classNode),
                ["name"] = classNode.Name,
                ["address"] = classNode.AddressFormula
            });
        }

        private JObject AddNode(JObject args)
        {
            var classId = args["class_id"]?.ToString() ?? args["class_name"]?.ToString();
            var nodeType = args["type"]?.ToString();
            var nodeName = args["name"]?.ToString();

            if (string.IsNullOrEmpty(classId))
                return Error("Missing 'class_id' or 'class_name' parameter");

            if (string.IsNullOrEmpty(nodeType))
                return Error("Missing 'type' parameter");

            var project = GetCurrentProject();
            if (project == null)
                return Error("No project loaded");

            var classNode = FindClass(project.Classes, classId);

            if (classNode == null)
                return Error($"Class not found: {classId}");

            var nodeTypeObj = GetNodeType(nodeType);
            if (nodeTypeObj == null)
                return Error($"Unknown node type: {nodeType}");

            BaseNode newNode = null;

            InvokeOnMainThread(() =>
            {
                newNode = CreateNodeInstance(nodeTypeObj);
                if (newNode != null)
                {
                    if (!string.IsNullOrEmpty(nodeName))
                    {
                        newNode.Name = nodeName;
                    }
                    classNode.AddNode(newNode);
                }
            });

            if (newNode == null)
                return Error("Failed to create node");

            return Success(new JObject
            {
                ["type"] = newNode.GetType().Name,
                ["name"] = newNode.Name,
                ["offset"] = newNode.Offset,
                ["size"] = newNode.MemorySize
            });
        }

        private JObject AddNodeAtOffset(JObject args)
        {
            var classId = args["class_id"]?.ToString() ?? args["class_name"]?.ToString();
            var nodeType = args["type"]?.ToString();
            var nodeName = args["name"]?.ToString();
            var comment = args["comment"]?.ToString();
            var overwrite = args["overwrite"]?.Value<bool>() ?? false;

            if (string.IsNullOrEmpty(classId))
                return Error("Missing 'class_id' or 'class_name' parameter");

            if (string.IsNullOrEmpty(nodeType))
                return Error("Missing 'type' parameter");

            if (!TryGetInt(args["offset"], out var offset) || offset < 0)
                return Error("Missing or invalid 'offset' parameter");

            var project = GetCurrentProject();
            if (project == null)
                return Error("No project loaded");

            var classNode = FindClass(project.Classes, classId);

            if (classNode == null)
                return Error($"Class not found: {classId}");

            var nodeTypeObj = GetNodeType(nodeType);
            if (nodeTypeObj == null)
                return Error($"Unknown node type: {nodeType}");

            BaseNode newNode = null;
            string error = null;
            var coveredNodes = 0;

            InvokeOnMainThread(() =>
            {
                newNode = CreateNodeInstance(nodeTypeObj);
                if (newNode == null)
                {
                    error = "Failed to create node";
                    return;
                }

                if (!string.IsNullOrEmpty(nodeName))
                {
                    newNode.Name = nodeName;
                }

                if (!string.IsNullOrEmpty(comment))
                {
                    newNode.Comment = comment;
                }

                error = AddNodeAtExactOffset(classNode, newNode, offset, overwrite, out coveredNodes);
                if (error == null)
                {
                    if (!string.IsNullOrEmpty(nodeName))
                    {
                        newNode.Name = nodeName;
                    }

                    if (!string.IsNullOrEmpty(comment))
                    {
                        newNode.Comment = comment;
                    }
                }
            });

            if (error != null)
                return Error(error);

            return Success(new JObject
            {
                ["type"] = newNode.GetType().Name,
                ["name"] = newNode.Name,
                ["offset"] = newNode.Offset,
                ["size"] = newNode.MemorySize,
                ["class_size"] = classNode.MemorySize,
                ["covered_nodes"] = coveredNodes
            });
        }

        private JObject AddClassInstanceAtOffset(JObject args)
        {
            var classId = args["class_id"]?.ToString() ?? args["class_name"]?.ToString();
            var targetClassId = args["target_class_id"]?.ToString() ??
                args["target_class"]?.ToString() ??
                args["type_class"]?.ToString();
            var nodeName = args["name"]?.ToString();
            var comment = args["comment"]?.ToString();
            var overwrite = args["overwrite"]?.Value<bool>() ?? false;

            if (string.IsNullOrEmpty(classId))
                return Error("Missing 'class_id' or 'class_name' parameter");

            if (string.IsNullOrEmpty(targetClassId))
                return Error("Missing 'target_class_id' parameter");

            if (!TryGetInt(args["offset"], out var offset) || offset < 0)
                return Error("Missing or invalid 'offset' parameter");

            var project = GetCurrentProject();
            if (project == null)
                return Error("No project loaded");

            var classNode = FindClass(project.Classes, classId);
            if (classNode == null)
                return Error($"Class not found: {classId}");

            var targetClass = FindClass(project.Classes, targetClassId);
            if (targetClass == null)
                return Error($"Target class not found: {targetClassId}");

            if (ClassUtil.IsCyclicIfClassIsAccessibleFromParent(classNode, targetClass, project.Classes))
                return Error($"Adding {targetClass.Name} to {classNode.Name} would create a class cycle");

            var newNode = new ClassInstanceNode();
            newNode.ChangeInnerNode(targetClass);

            var coveredNodes = 0;
            string error = null;

            InvokeOnMainThread(() =>
            {
                error = AddNodeAtExactOffset(classNode, newNode, offset, overwrite, out coveredNodes);
                if (error == null)
                {
                    if (!string.IsNullOrEmpty(nodeName))
                    {
                        newNode.Name = nodeName;
                    }

                    if (!string.IsNullOrEmpty(comment))
                    {
                        newNode.Comment = comment;
                    }
                }
            });

            if (error != null)
                return Error(error);

            return Success(new JObject
            {
                ["type"] = newNode.GetType().Name,
                ["name"] = newNode.Name,
                ["offset"] = newNode.Offset,
                ["size"] = newNode.MemorySize,
                ["inner_class"] = targetClass.Name,
                ["inner_uuid"] = GetClassUuid(targetClass),
                ["class_size"] = classNode.MemorySize,
                ["covered_nodes"] = coveredNodes
            });
        }

        private JObject RenameNode(JObject args)
        {
            var classId = args["class_id"]?.ToString() ?? args["class_name"]?.ToString();
            var nodeIndex = args["node_index"];
            var newName = args["name"]?.ToString();

            if (string.IsNullOrEmpty(classId))
                return Error("Missing 'class_id' or 'class_name' parameter");

            if (nodeIndex == null)
                return Error("Missing 'node_index' parameter");

            if (string.IsNullOrEmpty(newName))
                return Error("Missing 'name' parameter");

            var project = GetCurrentProject();
            if (project == null)
                return Error("No project loaded");

            var classNode = FindClass(project.Classes, classId);

            if (classNode == null)
                return Error($"Class not found: {classId}");

            var index = nodeIndex.Value<int>();
            if (index < 0 || index >= classNode.Nodes.Count)
                return Error($"Invalid node index: {index}");

            var node = classNode.Nodes[index];

            InvokeOnMainThread(() =>
            {
                node.Name = newName;
            });

            return Success(new JObject
            {
                ["name"] = node.Name,
                ["offset"] = node.Offset
            });
        }

        private JObject SetComment(JObject args)
        {
            var classId = args["class_id"]?.ToString() ?? args["class_name"]?.ToString();
            var nodeIndex = args["node_index"];
            var comment = args["comment"]?.ToString() ?? "";

            if (string.IsNullOrEmpty(classId))
                return Error("Missing 'class_id' or 'class_name' parameter");

            if (nodeIndex == null)
                return Error("Missing 'node_index' parameter");

            var project = GetCurrentProject();
            if (project == null)
                return Error("No project loaded");

            var classNode = FindClass(project.Classes, classId);

            if (classNode == null)
                return Error($"Class not found: {classId}");

            var index = nodeIndex.Value<int>();
            if (index < 0 || index >= classNode.Nodes.Count)
                return Error($"Invalid node index: {index}");

            var node = classNode.Nodes[index];

            InvokeOnMainThread(() =>
            {
                node.Comment = comment;
            });

            return Success();
        }

        private JObject ChangeNodeType(JObject args)
        {
            var classId = args["class_id"]?.ToString() ?? args["class_name"]?.ToString();
            var nodeIndex = args["node_index"];
            var newType = args["type"]?.ToString();

            if (string.IsNullOrEmpty(classId))
                return Error("Missing 'class_id' or 'class_name' parameter");

            if (nodeIndex == null)
                return Error("Missing 'node_index' parameter");

            if (string.IsNullOrEmpty(newType))
                return Error("Missing 'type' parameter");

            var project = GetCurrentProject();
            if (project == null)
                return Error("No project loaded");

            var classNode = FindClass(project.Classes, classId);

            if (classNode == null)
                return Error($"Class not found: {classId}");

            var index = nodeIndex.Value<int>();
            if (index < 0 || index >= classNode.Nodes.Count)
                return Error($"Invalid node index: {index}");

            var nodeTypeObj = GetNodeType(newType);
            if (nodeTypeObj == null)
                return Error($"Unknown node type: {newType}");

            var oldNode = classNode.Nodes[index];
            BaseNode newNode = null;

            InvokeOnMainThread(() =>
            {
                newNode = CreateNodeInstance(nodeTypeObj);
                if (newNode != null)
                {
                    classNode.ReplaceChildNode(oldNode, newNode);
                }
            });

            if (newNode == null)
                return Error("Failed to change node type");

            return Success(new JObject
            {
                ["type"] = newNode.GetType().Name,
                ["name"] = newNode.Name,
                ["offset"] = newNode.Offset,
                ["size"] = newNode.MemorySize
            });
        }

        private JObject GetProcessInfo()
        {
            if (!host.Process.IsValid)
                return Error("No process attached");

            var proc = host.Process.UnderlayingProcess;
            return Success(new JObject
            {
                ["id"] = proc.Id.ToInt64(),
                ["name"] = proc.Name,
                ["path"] = proc.Path,
                ["is_valid"] = host.Process.IsValid
            });
        }

        private Type GetNodeType(string typeName)
        {
            var nodeTypes = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
            {
                // Numeric types
                { "int8", typeof(Int8Node) },
                { "int16", typeof(Int16Node) },
                { "int32", typeof(Int32Node) },
                { "int64", typeof(Int64Node) },
                { "uint8", typeof(UInt8Node) },
                { "uint16", typeof(UInt16Node) },
                { "uint32", typeof(UInt32Node) },
                { "uint64", typeof(UInt64Node) },
                { "float", typeof(FloatNode) },
                { "double", typeof(DoubleNode) },
                // Hex types
                { "hex8", typeof(Hex8Node) },
                { "hex16", typeof(Hex16Node) },
                { "hex32", typeof(Hex32Node) },
                { "hex64", typeof(Hex64Node) },
                // Text types
                { "utf8text", typeof(Utf8TextNode) },
                { "utf16text", typeof(Utf16TextNode) },
                { "utf32text", typeof(Utf32TextNode) },
                { "utf8textptr", typeof(Utf8TextPtrNode) },
                { "utf16textptr", typeof(Utf16TextPtrNode) },
                { "utf32textptr", typeof(Utf32TextPtrNode) },
                // Vector types
                { "vector2", typeof(Vector2Node) },
                { "vector3", typeof(Vector3Node) },
                { "vector4", typeof(Vector4Node) },
                // Matrix types
                { "matrix3x3", typeof(Matrix3x3Node) },
                { "matrix3x4", typeof(Matrix3x4Node) },
                { "matrix4x4", typeof(Matrix4x4Node) },
                // Pointer type
                { "pointer", typeof(PointerNode) },
                // Function types
                { "function", typeof(FunctionNode) },
                { "functionptr", typeof(FunctionPtrNode) },
                // Virtual method
                { "virtualmethodtable", typeof(VirtualMethodTableNode) },
                // Bool
                { "bool", typeof(BoolNode) },
            };

            // Try direct lookup
            if (nodeTypes.TryGetValue(typeName, out var type))
                return type;

            // Try with "Node" suffix stripped
            if (typeName.EndsWith("Node", StringComparison.OrdinalIgnoreCase))
            {
                var baseName = typeName.Substring(0, typeName.Length - 4);
                if (nodeTypes.TryGetValue(baseName, out type))
                    return type;
            }

            return null;
        }

        private BaseNode CreateNodeInstance(Type nodeType)
        {
            if (nodeType == typeof(PointerNode))
            {
                return BaseNode.CreateInstanceFromType(nodeType, false);
            }

            return BaseNode.CreateInstanceFromType(nodeType);
        }

        private string AddNodeAtExactOffset(ClassNode classNode, BaseNode newNode, int offset, bool overwrite, out int coveredNodeCount)
        {
            coveredNodeCount = 0;

            var endOffset = offset + newNode.MemorySize;
            if (endOffset < offset)
                return "Offset plus node size overflowed";

            classNode.BeginUpdate();
            try
            {
                classNode.UpdateOffsets();

                if (endOffset > classNode.MemorySize)
                {
                    classNode.AddBytes(endOffset - classNode.MemorySize);
                    classNode.UpdateOffsets();
                }

                var boundaryError = EnsureNodeBoundary(classNode, offset);
                if (boundaryError != null)
                    return boundaryError;

                boundaryError = EnsureNodeBoundary(classNode, endOffset);
                if (boundaryError != null)
                    return boundaryError;

                classNode.UpdateOffsets();

                var coveredNodes = classNode.Nodes
                    .Where(n => n.Offset >= offset && n.Offset < endOffset)
                    .ToList();

                if (coveredNodes.Count == 0)
                    return $"No padding found at offset 0x{offset:X}";

                var coveredSize = coveredNodes.Sum(n => n.MemorySize);
                if (coveredSize != newNode.MemorySize)
                    return $"Internal layout error: covered size {coveredSize} does not match node size {newNode.MemorySize}";

                if (!overwrite)
                {
                    var nonPadding = coveredNodes.FirstOrDefault(n => !(n is BaseHexNode));
                    if (nonPadding != null)
                    {
                        return $"Offset range 0x{offset:X}-0x{endOffset:X} overlaps existing {nonPadding.GetType().Name} '{nonPadding.Name}' at 0x{nonPadding.Offset:X}. Pass overwrite=true to replace it.";
                    }
                }

                var firstNode = coveredNodes[0];
                classNode.ReplaceChildNode(firstNode, newNode);

                foreach (var node in coveredNodes.Skip(1))
                {
                    classNode.RemoveNode(node);
                }

                newNode.Offset = offset;
                coveredNodeCount = coveredNodes.Count;
                return null;
            }
            finally
            {
                classNode.EndUpdate();
            }
        }

        private string EnsureNodeBoundary(ClassNode classNode, int offset)
        {
            while (true)
            {
                classNode.UpdateOffsets();

                if (offset == classNode.MemorySize || classNode.Nodes.Any(n => n.Offset == offset))
                    return null;

                var containingNode = classNode.Nodes.FirstOrDefault(n => n.Offset < offset && offset < n.Offset + n.MemorySize);
                if (containingNode == null)
                    return $"Unable to locate node boundary for offset 0x{offset:X}";

                if (!(containingNode is BaseHexNode))
                    return $"Offset 0x{offset:X} falls inside existing {containingNode.GetType().Name} '{containingNode.Name}' at 0x{containingNode.Offset:X}";

                var leadingSize = offset - containingNode.Offset;
                var paddingNode = CreatePaddingNode(leadingSize);
                if (paddingNode == null)
                    return $"Unable to split padding at offset 0x{offset:X}";

                classNode.ReplaceChildNode(containingNode, paddingNode);
            }
        }

        private BaseNode CreatePaddingNode(int maxSize)
        {
            if (maxSize <= 0)
                return null;

            if (IntPtr.Size == 8 && maxSize >= 8)
                return new Hex64Node();

            if (maxSize >= 4)
                return new Hex32Node();

            if (maxSize >= 2)
                return new Hex16Node();

            return new Hex8Node();
        }

        private bool TryGetInt(JToken token, out int value)
        {
            value = 0;
            if (token == null)
                return false;

            if (token.Type == JTokenType.Integer)
            {
                value = token.Value<int>();
                return true;
            }

            var text = token.ToString();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return int.TryParse(text.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out value);
            }

            return int.TryParse(text, out value);
        }

        private class NodeQueryOptions
        {
            public int? Offset { get; private set; }
            public int? RangeEnd { get; private set; }
            public int StartIndex { get; private set; }
            public int Limit { get; private set; }
            public bool IncludePadding { get; private set; }
            public string Error { get; private set; }

            public static NodeQueryOptions FromArgs(JObject args)
            {
                var options = new NodeQueryOptions
                {
                    StartIndex = 0,
                    Limit = 500,
                    IncludePadding = true
                };

                if (TryReadInt(args["start_index"], out var startIndex))
                {
                    if (startIndex < 0)
                    {
                        options.Error = "Invalid 'start_index' parameter";
                        return options;
                    }

                    options.StartIndex = startIndex;
                }

                if (TryReadInt(args["limit"], out var limit))
                {
                    if (limit <= 0 || limit > 5000)
                    {
                        options.Error = "'limit' must be between 1 and 5000";
                        return options;
                    }

                    options.Limit = limit;
                }

                if (TryReadInt(args["offset"], out var offset))
                {
                    if (offset < 0)
                    {
                        options.Error = "Invalid 'offset' parameter";
                        return options;
                    }

                    options.Offset = offset;
                }

                if (TryReadInt(args["size"], out var size))
                {
                    if (size <= 0)
                    {
                        options.Error = "Invalid 'size' parameter";
                        return options;
                    }

                    if (options.Offset == null)
                    {
                        options.Error = "'size' requires 'offset'";
                        return options;
                    }

                    var rangeEnd = options.Offset.Value + size;
                    if (rangeEnd < options.Offset.Value)
                    {
                        options.Error = "Offset plus size overflowed";
                        return options;
                    }

                    options.RangeEnd = rangeEnd;
                }

                if (args["include_padding"] != null)
                {
                    options.IncludePadding = args["include_padding"].Value<bool>();
                }

                return options;
            }

            private static bool TryReadInt(JToken token, out int value)
            {
                value = 0;
                if (token == null)
                    return false;

                if (token.Type == JTokenType.Integer)
                {
                    value = token.Value<int>();
                    return true;
                }

                var text = token.ToString();
                if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    return int.TryParse(text.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out value);
                }

                return int.TryParse(text, out value);
            }
        }

        private ClassNode FindClass(IReadOnlyList<ClassNode> classes, string identifier)
        {
            var classNode = classes.FirstOrDefault(c => ClassUuidMatches(c, identifier));
            if (classNode != null)
                return classNode;

            return classes.FirstOrDefault(c =>
                c.Name.Equals(identifier, StringComparison.OrdinalIgnoreCase));
        }

        private string GetClassUuid(ClassNode classNode)
        {
            var uuid = GetClassUuidValue(classNode);
            return uuid?.ToString() ?? "";
        }

        private object GetClassUuidValue(ClassNode classNode)
        {
            return ClassUuidProperty?.GetValue(classNode, null);
        }

        private bool ClassUuidMatches(ClassNode classNode, string identifier)
        {
            if (string.IsNullOrEmpty(identifier))
                return false;

            var uuid = GetClassUuidValue(classNode);
            if (uuid == null)
                return false;

            foreach (var candidate in GetUuidIdentifiers(uuid))
            {
                if (string.Equals(candidate, identifier, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            if (uuid is Guid uuidGuid && Guid.TryParse(identifier, out var identifierGuid))
                return uuidGuid.Equals(identifierGuid);

            return false;
        }

        private IEnumerable<string> GetUuidIdentifiers(object uuid)
        {
            var value = uuid.ToString();
            if (!string.IsNullOrEmpty(value))
                yield return value;

            if (uuid is Guid guid)
            {
                yield return guid.ToString("N");
                yield return guid.ToString("B");
                yield break;
            }

            foreach (var methodName in new[] { "ToHexString", "ToBase64String" })
            {
                var method = uuid.GetType().GetMethod(methodName, Type.EmptyTypes);
                if (method?.ReturnType != typeof(string))
                    continue;

                var identifier = method.Invoke(uuid, null) as string;
                if (!string.IsNullOrEmpty(identifier))
                    yield return identifier;
            }
        }

        private ReClassNET.Project.ReClassNetProject GetCurrentProject()
        {
            ReClassNET.Project.ReClassNetProject project = null;

            if (host.MainWindow.InvokeRequired)
            {
                host.MainWindow.Invoke(new Action(() =>
                {
                    project = host.MainWindow.CurrentProject;
                }));
            }
            else
            {
                project = host.MainWindow.CurrentProject;
            }

            return project;
        }

        private void InvokeOnMainThread(Action action)
        {
            if (host.MainWindow.InvokeRequired)
            {
                host.MainWindow.Invoke(action);
            }
            else
            {
                action();
            }
        }
    }
}
