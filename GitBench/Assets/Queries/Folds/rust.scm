; What folds besides a declaration. Only a construct spanning three lines or more survives, and
; where several start on one line the widest wins. A fold ending on a closing bracket pulls that
; line up behind its chip; one ending on content hides it with the rest.

(block) @fold
(declaration_list) @fold
(field_declaration_list) @fold
(ordered_field_declaration_list) @fold
(enum_variant_list) @fold
(field_initializer_list) @fold
(match_block) @fold
(use_list) @fold

(arguments) @fold
(parameters) @fold
(array_expression) @fold
(tuple_expression) @fold
(token_tree) @fold

(raw_string_literal) @fold
(string_literal) @fold
(block_comment) @fold
