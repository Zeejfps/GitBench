; The document's element tree, which is what an HTML outline is.
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
