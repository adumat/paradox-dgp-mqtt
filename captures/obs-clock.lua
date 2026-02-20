obs = obslua

source_name = ""

function script_description()
  return "Shows a real-time clock with milliseconds in a Text source."
end

function script_properties()
  local props = obs.obs_properties_create()
  local p = obs.obs_properties_add_list(props, "source", "Text Source",
    obs.OBS_COMBO_TYPE_EDITABLE, obs.OBS_COMBO_FORMAT_STRING)
  local sources = obs.obs_enum_sources()
  if sources then
    for _, source in ipairs(sources) do
      local id = obs.obs_source_get_unversioned_id(source)
      if id == "text_gdiplus" or id == "text_ft2_source" then
        local name = obs.obs_source_get_name(source)
        obs.obs_property_list_add_string(p, name, name)
      end
    end
    obs.source_list_release(sources)
  end
  return props
end

function script_update(settings)
  source_name = obs.obs_data_get_string(settings, "source")
end

function script_tick(seconds)
  if source_name == "" then return end
  local source = obs.obs_get_source_by_name(source_name)
  if source then
    local now = os.clock()
    local ms = math.floor((now % 1) * 1000)
    local text = os.date("%H:%M:%S") .. string.format(".%03d", ms)
    local settings = obs.obs_data_create()
    obs.obs_data_set_string(settings, "text", text)
    obs.obs_source_update(source, settings)
    obs.obs_data_release(settings)
    obs.obs_source_release(source)
  end
end
