(function_definition
  declarator: (function_declarator
    declarator: (identifier) @name)
  body: (_) @body) @def.function

; A prototype: declared, never defined here, so it correctly reports as not foldable.
(declaration
  declarator: (function_declarator
    declarator: (identifier) @name)) @def.function

(struct_specifier
  name: (type_identifier) @name
  body: (_) @body) @def.struct

(union_specifier
  name: (type_identifier) @name
  body: (_) @body) @def.struct

(enum_specifier
  name: (type_identifier) @name
  body: (_) @body) @def.enum

(enumerator
  name: (identifier) @name
  "="? @body) @def.enum_member

(type_definition
  declarator: (type_identifier) @name) @def.type

(field_declaration
  declarator: (field_identifier) @name) @def.field
