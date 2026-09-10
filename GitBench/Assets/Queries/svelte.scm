; The element tree, as in HTML: the grammar is HTML's with template syntax added.
(element
  (start_tag
    (tag_name) @name) @body) @def.type

(element
  (self_closing_tag
    (tag_name) @name)) @def.type

(script_element
  (start_tag
    (tag_name) @name) @body) @def.type

(style_element
  (start_tag
    (tag_name) @name) @body) @def.type

; A snippet is the template's function: named, parameterized, and rendered by name.
(snippet_statement
  (snippet_start
    (snippet_name) @name) @body) @def.function
