(class_declaration
  name: (identifier) @name
  body: (_)? @body) @def.class

(method_definition
  name: (_) @name
  body: (_)? @body) @def.method

(field_definition
  property: (_) @name
  value: (_)? @body) @def.field

(function_declaration
  name: (identifier) @name
  body: (_)? @body) @def.function

(generator_function_declaration
  name: (identifier) @name
  body: (_)? @body) @def.function

; `const Thing = () => {}` at the top level only — the locals inside every function body would
; drown the outline otherwise. Disjoint from the value pattern below by structure, not by order:
; matches arrive in source order, so two patterns on one node would race.
(program
  (lexical_declaration
    (variable_declarator
      name: (identifier) @name
      value: (arrow_function
        body: (_) @body)) @def.function))

(export_statement
  declaration: (lexical_declaration
    (variable_declarator
      name: (identifier) @name
      value: (arrow_function
        body: (_) @body)) @def.function))

(program
  (lexical_declaration
    (variable_declarator
      name: (identifier) @name
      value: (_ !body) @body) @def.field))

(export_statement
  declaration: (lexical_declaration
    (variable_declarator
      name: (identifier) @name
      value: (_ !body) @body) @def.field))
