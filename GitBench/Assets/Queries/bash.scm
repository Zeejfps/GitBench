(function_definition
  name: (word) @name
  body: (_)? @body) @def.function

; Top-level assignments only: the configuration a script opens with, not every local in a loop.
(program
  (variable_assignment
    name: (variable_name) @name
    value: (_)? @body) @def.field)
