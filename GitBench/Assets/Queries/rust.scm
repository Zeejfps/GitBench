(mod_item
  name: (identifier) @name
  body: (_)? @body) @def.namespace

(struct_item
  name: (type_identifier) @name
  body: (_)? @body) @def.struct

(union_item
  name: (type_identifier) @name
  body: (_)? @body) @def.struct

(enum_item
  name: (type_identifier) @name
  body: (_)? @body) @def.enum

(enum_variant
  name: (identifier) @name) @def.enum_member

(trait_item
  name: (type_identifier) @name
  body: (_)? @body) @def.interface

; An impl block is named by the type it implements for; the trait, when there is one, is part of
; the same story but not of the name.
(impl_item
  type: (_) @name
  body: (_)? @body) @def.class

(function_item
  name: (identifier) @name
  body: (_)? @body) @def.function

(function_signature_item
  name: (identifier) @name) @def.function

(type_item
  name: (type_identifier) @name
  type: (_)? @body) @def.type

(const_item
  name: (identifier) @name
  value: (_)? @body) @def.field

(static_item
  name: (identifier) @name
  value: (_)? @body) @def.field

(field_declaration
  name: (field_identifier) @name) @def.field

(macro_definition
  name: (identifier) @name) @def.function
