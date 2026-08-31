(function_declaration
  name: (identifier) @name
  body: (_)? @body) @def.function

(method_declaration
  name: (field_identifier) @name
  body: (_)? @body) @def.method

(type_declaration
  (type_spec
    name: (type_identifier) @name
    type: (struct_type
      (field_declaration_list) @body)) @def.struct)

(type_declaration
  (type_spec
    name: (type_identifier) @name
    type: (interface_type) @body) @def.interface)

(type_declaration
  (type_spec
    name: (type_identifier) @name
    type: [
      (type_identifier)
      (qualified_type)
      (pointer_type)
      (array_type)
      (slice_type)
      (map_type)
      (channel_type)
      (function_type)
      (generic_type)
    ] @body) @def.type)

(field_declaration
  name: (field_identifier) @name) @def.field

(const_spec
  name: (identifier) @name) @def.field

(var_spec
  name: (identifier) @name) @def.field
