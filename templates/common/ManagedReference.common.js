// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
var common = require('./common.js');
var classCategory = 'class';
var namespaceCategory = 'ns';

exports.transform = function (model) {

  if (!model) return null;

  langs = model.langs;
  var showAppliesTo = !model._disableApiAppliesTo && getAppliesToFrameworks(model).length > 0;
  var appliesToOptions = { names: model._frameworkNames, hideFrameworks: !!model._disableApiAppliesToFrameworks };
  handleItem(model, model._gitContribute, model._gitUrlPattern);
  if (showAppliesTo) handleAppliesTo(model, null, appliesToOptions);
  else delete model.variants;
  if (model.children) {
    model.children.forEach(function (item) {
      handleItem(item, model._gitContribute, model._gitUrlPattern);
      if (showAppliesTo) handleAppliesTo(item, model, appliesToOptions);
      else delete item.variants;
    });
  }

  if (model.type) {
    switch (model.type.toLowerCase()) {
      case 'namespace':
        model.isNamespace = true;
        if (model.children) groupChildren(model, namespaceCategory);
        break;
      case 'class':
      case 'interface':
      case 'struct':
      case 'delegate':
      case 'enum':
        model.isClass = true;
        if (model.children) groupChildren(model, classCategory);
        model[getTypePropertyName(model.type)] = true;
        break;
      default:
        break;
    }
  }

  if (model.summary && !model.description) {
    model.description = model.summary.replace(/<.*?>/gi, '').replace(/(\r\n|\n|\r)/gm, ' ').trim();
  }

  return model;
}

exports.getBookmarks = function (model, ignoreChildren) {
  if (!model || !model.type || model.type.toLowerCase() === "namespace") return null;

  var bookmarks = {};

  if (typeof ignoreChildren == 'undefined' || ignoreChildren === false) {
    if (model.children) {
      model.children.forEach(function (item) {
        bookmarks[item.uid] = common.getHtmlId(item.uid);
        if (item.overload && item.overload.uid) {
          bookmarks[item.overload.uid] = common.getHtmlId(item.overload.uid);
        }
      });
    }
  }

  // Reference's first level bookmark should have no anchor
  bookmarks[model.uid] = "";
  return bookmarks;
}

exports.groupChildren = groupChildren;
exports.getTypePropertyName = getTypePropertyName;
exports.getCategory = getCategory;

// The units an item applies to: a framework, or `value:framework` when there is an axis such as EF Core.
function getAppliesToFrameworks(vm) {
  var units = [];
  (vm.appliesTo || []).forEach(function (group) {
    (group.frameworks || []).forEach(function (framework) {
      var unit = group.version ? group.version + ':' + framework : framework;
      if (units.indexOf(unit) < 0) {
        units.push(unit);
      }
    });
  });
  return units;
}

// One entry per value of the axis, or a single one without an axis, rendered by `partials/appliesTo`.
// Every field is set, so that mustache does not look one up on the enclosing item.
function getAppliesToGroups(vm, options) {
  return (vm.appliesTo || []).map(function (group, index) {
    var label = group.version ? (group.label || group.version) : "";
    var frameworks = label && options.hideFrameworks
      ? ""
      : (group.frameworks || []).map(function (framework) { return getFrameworkLabel(framework, options.names); }).join(', ');
    return { appliesToLabel: label, appliesToFrameworks: frameworks, appliesToIsFirst: index === 0 };
  });
}

function getFrameworkLabel(framework, names) {
  if (names && Object.prototype.hasOwnProperty.call(names, framework)) return names[framework];

  var match = /^netstandard(\d+\.\d+)$/.exec(framework);
  if (match) return '.NET Standard ' + match[1];

  match = /^netcoreapp(\d+\.\d+)$/.exec(framework);
  if (match) return '.NET Core ' + match[1];

  // .NET 5 and later are named by their major version, as Microsoft does: `net8.0` is .NET 8.
  match = /^net(\d+)\.(\d+)(-.+)?$/.exec(framework);
  if (match) return '.NET ' + match[1] + (match[2] === '0' ? '' : '.' + match[2]) + (match[3] || '');

  match = /^net(\d)(\d)(\d)?$/.exec(framework);
  if (match) return '.NET Framework ' + match[1] + '.' + match[2] + (match[3] ? '.' + match[3] : '');

  return framework;
}

function handleAppliesTo(vm, parent, options) {
  // set to empty so that mustache does not look up the values of the enclosing type
  vm.appliesToData = "";
  vm.appliesToGroups = [];
  vm.showAppliesTo = false;
  vm.showAppliesToBadge = false;

  vm.variants = vm.variants || [];
  vm.variants.forEach(function (variant) {
    variant.appliesToData = getAppliesToFrameworks(variant).join(' ');
    variant.appliesToGroups = getAppliesToGroups(variant, options);
    // a field that does not differ must not be looked up on the item, which would repeat its value
    variant.summary = variant.summary || "";
    variant.remarks = variant.remarks || "";
    variant.syntax = variant.syntax || "";
    variant.inheritance = variant.inheritance || [];
  });

  var frameworks = getAppliesToFrameworks(vm);
  if (frameworks.length === 0) {
    return;
  }

  vm.appliesToData = frameworks.join(' ');
  vm.appliesToGroups = getAppliesToGroups(vm, options);
  if (!parent) {
    vm.showAppliesTo = true;
  } else if (frameworks.slice().sort().join(' ') !== getAppliesToFrameworks(parent).sort().join(' ')) {
    // A member is only marked when it differs from its type, or every member would carry the same badge.
    vm.showAppliesToBadge = true;
  }
}

function groupChildren(model, category) {
  if (!model || !model.type) {
    return;
  }
  var typeChildrenItems = getDefinitions(category);
  var grouped = {};

  model.children.forEach(function (c) {
    if (c.isEii) {
      var type = "eii";
    } else {
      var type = c.type.toLowerCase();
    }
    if (!grouped.hasOwnProperty(type)) {
      grouped[type] = [];
    }
    // special handle for field
    if (type === "field" && c.syntax) {
      c.syntax.fieldValue = c.syntax.return;
      c.syntax.return = undefined;
    }
    // special handle for property
    if ((type === "property" || type === "attachedproperty") && c.syntax) {
      c.syntax.propertyValue = c.syntax.return;
      c.syntax.return = undefined;
    }
    // special handle for event
    if ((type === "event" || type === "attachedevent") && c.syntax) {
      c.syntax.eventType = c.syntax.return;
      c.syntax.return = undefined;
    }
    grouped[type].push(c);
  })

  var children = [];
  for (var key in typeChildrenItems) {
    if (typeChildrenItems.hasOwnProperty(key) && grouped.hasOwnProperty(key)) {
      var typeChildrenItem = typeChildrenItems[key];
      var items = grouped[key];
      if (items && items.length > 0) {
        var item = {};
        for (var itemKey in typeChildrenItem) {
          if (typeChildrenItem.hasOwnProperty(itemKey)) {
            item[itemKey] = typeChildrenItem[itemKey];
          }
        }
        item.children = items;
        children.push(item);
      }
    }
  }

  model.children = children;
}

function getTypePropertyName(type) {
  if (!type) {
    return undefined;
  }
  var loweredType = type.toLowerCase();
  var definition = getDefinition(loweredType);
  if (definition) {
    return definition.typePropertyName;
  }

  return undefined;
}

function getCategory(type) {
  var classItems = getDefinitions(classCategory);
  if (classItems.hasOwnProperty(type)) {
    return classCategory;
  }

  var namespaceItems = getDefinitions(namespaceCategory);
  if (namespaceItems.hasOwnProperty(type)) {
    return namespaceCategory;
  }
  return undefined;
}

function getDefinition(type) {
  var classItems = getDefinitions(classCategory);
  if (classItems.hasOwnProperty(type)) {
    return classItems[type];
  }
  var namespaceItems = getDefinitions(namespaceCategory);
  if (namespaceItems.hasOwnProperty(type)) {
    return namespaceItems[type];
  }
  return undefined;
}

function getDefinitions(category) {
  var namespaceItems = {
    "namespace":    { inNamespace: true,    typePropertyName: "inNamespace",    id: "namespaces" },
    "class":        { inClass: true,        typePropertyName: "inClass",        id: "classes" },
    "struct":       { inStruct: true,       typePropertyName: "inStruct",       id: "structs" },
    "interface":    { inInterface: true,    typePropertyName: "inInterface",    id: "interfaces" },
    "enum":         { inEnum: true,         typePropertyName: "inEnum",         id: "enums" },
    "delegate":     { inDelegate: true,     typePropertyName: "inDelegate",     id: "delegates" }
  };
  var classItems = {
    "constructor":      { inConstructor: true,      typePropertyName: "inConstructor",      id: "constructors" },
    "field":            { inField: true,            typePropertyName: "inField",            id: "fields" },
    "property":         { inProperty: true,         typePropertyName: "inProperty",         id: "properties" },
    "attachedproperty": { inAttachedProperty: true, typePropertyName: "inAttachedProperty", id: "attachedProperties" },
    "method":           { inMethod: true,           typePropertyName: "inMethod",           id: "methods" },
    "event":            { inEvent: true,            typePropertyName: "inEvent",            id: "events" },
    "attachedevent":    { inAttachedEvent: true,    typePropertyName: "inAttachedEvent",    id: "attachedEvents" },
    "operator":         { inOperator: true,         typePropertyName: "inOperator",         id: "operators" },
    "eii":              { inEii: true,              typePropertyName: "inEii",              id: "eii" }
  };
  if (category === 'class') {
    return classItems;
  }
  if (category === 'ns') {
    return namespaceItems;
  }
  console.err("category '" + category + "' is not valid.");
  return undefined;
}

function handleItem(vm, gitContribute, gitUrlPattern) {
  // get contribution information
  vm.docurl = common.getImproveTheDocHref(vm, gitContribute, gitUrlPattern);
  vm.sourceurl = common.getViewSourceHref(vm, null, gitUrlPattern);

  // set to null incase mustache looks up
  vm.summary = vm.summary || "";
  vm.description = vm.description || "";
  vm.remarks = vm.remarks || "";
  vm.conceptual = vm.conceptual || "";
  vm.syntax = vm.syntax || "";
  vm.implements = vm.implements || "";
  vm.example = vm.example || "";
  vm.seealso = vm.seealso || [];
  common.processSeeAlso(vm);

  // id is used as default template's bookmark
  vm.id = common.getHtmlId(vm.uid);
  if (vm.overload && vm.overload.uid) {
    vm.overload.id = common.getHtmlId(vm.overload.uid);
  }

  if (vm.supported_platforms) {
    vm.supported_platforms = transformDictionaryToArray(vm.supported_platforms);
  }

  if (vm.requirements) {
    var type = vm.type.toLowerCase();
    if (type == "method") {
      vm.requirements_method = transformDictionaryToArray(vm.requirements);
    } else {
      vm.requirements = transformDictionaryToArray(vm.requirements);
    }
  }

  if (vm && langs) {
    if (shouldHideTitleType(vm)) {
      vm.hideTitleType = true;
    } else {
      vm.hideTitleType = false;
    }

    if (shouldHideSubtitle(vm)) {
      vm.hideSubtitle = true;
    } else {
      vm.hideSubtitle = false;
    }
  }

  function shouldHideTitleType(vm) {
    var type = vm.type.toLowerCase();
    return ((type === 'namespace' && langs.length == 1 && (langs[0] === 'objectivec' || langs[0] === 'java' || langs[0] === 'c'))
      || ((type === 'class' || type === 'enum') && langs.length == 1 && langs[0] === 'c'));
  }

  function shouldHideSubtitle(vm) {
    var type = vm.type.toLowerCase();
    return (type === 'class' || type === 'namespace') && langs.length == 1 && langs[0] === 'c';
  }

  function transformDictionaryToArray(dic) {
    var array = [];
    for (var key in dic) {
      if (dic.hasOwnProperty(key)) {
        array.push({ "name": key, "value": dic[key] })
      }
    }

    return array;
  }
  if(vm.syntax.typeParameters) {
    vm.syntax.typeParameters.forEach(item => {
      item.description = item.description || "";
    });
  }
}
