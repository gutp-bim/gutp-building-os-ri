variable "namespace" { type = string }
variable "storage_class" { type = string }
variable "storage_size" { type = string }
variable "image_tag" { type = string; default = "v0.3.22" }
variable "labels" { type = map(string); default = {} }
