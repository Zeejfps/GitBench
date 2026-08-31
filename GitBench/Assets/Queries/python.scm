(class_definition
  name: (identifier) @name
  body: (_)? @body) @def.class

; A method is a function inside a class, which the outline's nesting already says. Python's grammar
; draws no distinction between the two and neither does this.
(function_definition
  name: (identifier) @name
  body: (_)? @body) @def.function

; Module-level bindings only: constants and the settings tables a reader navigates to.
(module
  (expression_statement
    (assignment
      left: (identifier) @name
      right: (_)? @body) @def.field))
