; Namespaces. `module` covers `namespace X {}` and `module X {}`; `internal_module` is
; the nested form.
(module
  name: (_) @name
  body: (_) @body) @def.namespace

(internal_module
  name: (_) @name
  body: (_) @body) @def.namespace

(class_declaration
  name: (type_identifier) @name
  body: (_)? @body) @def.class

(abstract_class_declaration
  name: (type_identifier) @name
  body: (_)? @body) @def.class

(interface_declaration
  name: (type_identifier) @name
  body: (_)? @body) @def.interface

(enum_declaration
  name: (identifier) @name
  body: (_)? @body) @def.enum

(enum_body
  (enum_assignment
    name: (_) @name
    "=" @body) @def.enum_member)

; A member with no initializer is its own name: the identifier is the whole declaration.
(enum_body
  ((property_identifier) @name) @def.enum_member)

(type_alias_declaration
  name: (type_identifier) @name
  "=" @body) @def.type

(function_declaration
  name: (identifier) @name
  body: (_)? @body) @def.function

(generator_function_declaration
  name: (identifier) @name
  body: (_)? @body) @def.function

; An overload signature, which declares without a body.
(function_signature
  name: (identifier) @name) @def.function

(method_definition
  name: (_) @name
  body: (_)? @body) @def.method

; Anchored to what declares them. An unanchored signature pattern also matches the members of an
; inline object type — `(props: { label: string })` — and a parameter's shape is not a declaration
; anyone navigates to.
(interface_body
  (method_signature
    name: (_) @name) @def.method)

(type_alias_declaration
  value: (object_type
    (method_signature
      name: (_) @name) @def.method))

(abstract_method_signature
  name: (_) @name) @def.method

(public_field_definition
  name: (_) @name
  value: (_)? @body) @def.field

(interface_body
  (property_signature
    name: (_) @name
    type: (_)? @body) @def.property)

(type_alias_declaration
  value: (object_type
    (property_signature
      name: (_) @name
      type: (_)? @body) @def.property))

; `const Thing = () => {}` is how modern TypeScript declares most of its functions and every
; React component, so it earns a node — but only at the top level, or the locals inside every
; function body would drown the outline. The arrow patterns come first so deduplication keeps
; them: a match on the same node loses to the one already recorded.
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

; Anything else bound at the top level — a config object, a table of constants. Disjoint from the
; patterns above by structure rather than by order: a value with no `body` field is not a function,
; and two patterns matching one node would otherwise race, since matches arrive in source order
; rather than in the order the patterns are written.
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

(program
  (lexical_declaration
    (variable_declarator
      name: (identifier) @name
      !value) @def.field))

(export_statement
  declaration: (lexical_declaration
    (variable_declarator
      name: (identifier) @name
      !value) @def.field))
