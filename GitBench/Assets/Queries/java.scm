(class_declaration
  name: (identifier) @name
  body: (_)? @body) @def.class

(interface_declaration
  name: (identifier) @name
  body: (_)? @body) @def.interface

(annotation_type_declaration
  name: (identifier) @name
  body: (_)? @body) @def.interface

(enum_declaration
  name: (identifier) @name
  body: (_)? @body) @def.enum

(enum_constant
  name: (identifier) @name) @def.enum_member

(record_declaration
  name: (identifier) @name
  body: (_)? @body) @def.record

(method_declaration
  name: (identifier) @name
  body: (_)? @body
  ";"? @body) @def.method

(constructor_declaration
  name: (identifier) @name
  body: (_)? @body) @def.constructor

(compact_constructor_declaration
  name: (identifier) @name
  body: (_)? @body) @def.constructor

(field_declaration
  declarator: (variable_declarator
    name: (identifier) @name
    "="? @body)) @def.field
