; The grammar names no fields, so each shape is told apart by the children it has. A node matched
; by two patterns keeps the first, which is why interface and enum come before plain class.

(class_declaration
  "interface"
  (type_identifier) @name
  (class_body)? @body) @def.interface

(class_declaration
  (type_identifier) @name
  (enum_class_body) @body) @def.enum

(class_declaration
  (type_identifier) @name
  (class_body)? @body) @def.class

(object_declaration
  (type_identifier) @name
  (class_body)? @body) @def.class

(companion_object
  (type_identifier) @name
  (class_body)? @body) @def.class

(enum_entry
  (simple_identifier) @name) @def.enum_member

(secondary_constructor
  "constructor" @name
  (statements)? @body) @def.constructor

(function_declaration
  (simple_identifier) @name
  (function_body)? @body) @def.function

; A local `val` is a property_declaration too, so only the ones a file or a class declares count.
(source_file
  (property_declaration
    (variable_declaration
      (simple_identifier) @name)
    "="? @body) @def.property)

(class_body
  (property_declaration
    (variable_declaration
      (simple_identifier) @name)
    "="? @body) @def.property)

(type_alias
  (type_identifier) @name) @def.type
