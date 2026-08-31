(namespace_declaration
  name: (_) @name
  body: (_) @body) @def.namespace

(compilation_unit
  (file_scoped_namespace_declaration
    name: (_) @name
    ";" @body) @def.namespace) @extent

(class_declaration
  name: (identifier) @name
  body: (_)? @body) @def.class

(struct_declaration
  name: (identifier) @name
  body: (_)? @body) @def.struct

(interface_declaration
  name: (identifier) @name
  body: (_)? @body) @def.interface

(record_declaration
  name: (identifier) @name
  body: (_)? @body
  ";"? @body) @def.record

(enum_declaration
  name: (identifier) @name
  body: (_)? @body) @def.enum

(method_declaration
  name: (identifier) @name
  body: (_)? @body
  ";"? @body) @def.method

(constructor_declaration
  name: (identifier) @name
  body: (_)? @body
  ";"? @body) @def.constructor

(destructor_declaration
  name: (identifier) @name
  body: (_)? @body
  ";"? @body) @def.method

(operator_declaration
  operator: _ @name
  body: (_)? @body
  ";"? @body) @def.method

(conversion_operator_declaration
  type: (_) @name
  body: (_)? @body
  ";"? @body) @def.method

(property_declaration
  name: (identifier) @name
  accessors: (_)? @body
  value: (_)? @body) @def.property

(event_declaration
  name: (identifier) @name
  accessors: (_)? @body
  ";"? @body) @def.event

(enum_member_declaration
  name: (identifier) @name) @def.enum_member

(delegate_declaration
  name: (identifier) @name
  ";" @body) @def.type

(field_declaration
  (variable_declaration
    (variable_declarator
      name: (identifier) @name
      "="? @body))
  ";" @body) @def.field

(event_field_declaration
  (variable_declaration
    (variable_declarator
      name: (identifier) @name
      "="? @body))
  ";" @body) @def.event

(local_function_statement
  name: (identifier) @name
  body: (_)? @body) @def.function
