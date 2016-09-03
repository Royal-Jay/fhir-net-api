﻿using Hl7.Fhir.Model;
using Hl7.Fhir.Specification.Navigation;
using Hl7.Fhir.Specification.Source;
using Hl7.Fhir.Support;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Hl7.Fhir.Specification.Snapshot
{
    partial class SnapshotGenerator
    {
        /// <summary>(Re-)generate the <see cref="ElementDefinition.Base"/> components.</summary>
        /// <param name="elements">A list of <see cref="ElementDefinition"/> instances.</param>
        /// <param name="baseProfileUrl">The canonical url of the base profile, as defined by the <see cref="StructureDefinition.Base"/> property.</param>
        public void GenerateElementBase(IList<ElementDefinition> elements, string baseProfileUrl)
        {
            var nav = new ElementDefinitionNavigator(elements);
            if (nav.MoveToFirstChild() && !string.IsNullOrEmpty(baseProfileUrl))
            {
                var sd = _source.GetStructureDefinition(baseProfileUrl);
                if (sd != null)
                {
                    ensureSnapshot(sd, true);
                    GenerateElementBase(sd.Snapshot.Element, sd.Base);

                    var baseNav = new ElementDefinitionNavigator(sd);
                    if (baseNav.MoveToFirstChild())
                    {
                        // Special handling for the root element - always derived from the root element of the immediate base profile
                        ensureElementBase(nav.Current, baseNav.Current, true);

                        if (nav.MoveToFirstChild() && baseNav.MoveToFirstChild())
                        {
                            do
                            {
                                generateElementBase(nav, baseNav);
                            } while (nav.MoveToNext());
                        }
                    }

                }
                else
                {
                    if (!_settings.IgnoreUnresolvedProfiles)
                    {
                        throw Error.ResourceReferenceNotFoundException(
                            baseProfileUrl,
                            "Unresolved profile reference. Cannot locate the type profile for element '{0}'.\r\nProfile url = '{1}'".FormatWith(nav.Path, baseProfileUrl)
                        );
                    }
                    _invalidProfiles.Add(sd.Url, SnapshotProfileStatus.Missing);
                }
            }
        }

        private void generateElementBase(ElementDefinitionNavigator nav, ElementDefinitionNavigator baseNav)
        {
            // Debug.Print("[generateElementBase] Path = {0}  Base = {1}".FormatWith(nav.Path, baseNav.Path));
            var elem = nav.Current;
            Debug.Assert(elem != null);

            // Determine if the current element matches the current base element
            if (baseNav.PathName == nav.PathName || ElementDefinitionNavigator.IsRenamedChoiceElement(baseNav.PathName, nav.PathName))
            {
                // Match!

                // Initialize Base component
                ensureElementBase(elem, baseNav.Current);

                // Recurse child elements
                var navBm = nav.Bookmark();
                var baseNavBm = baseNav.Bookmark();
                if (nav.MoveToFirstChild() && baseNav.MoveToFirstChild())
                {
                    do
                    {
                        generateElementBase(nav, baseNav);
                    } while (nav.MoveToNext());

                    nav.ReturnToBookmark(navBm);
                    baseNav.ReturnToBookmark(baseNavBm);
                }

                // Consume the matched base element
                baseNav.MoveToNext();

                return;
            }
            else
            {
                // Drill down base profile
                var baseUrl = baseNav.StructureDefinition.Base;
                if (baseUrl != null)
                {
                    var baseDef = _source.GetStructureDefinition(baseUrl);
                    if (baseDef != null)
                    {
                        ensureSnapshot(baseDef, true);
                        GenerateElementBase(baseDef.Snapshot.Element, baseDef.Base);

                        baseNav = new ElementDefinitionNavigator(baseDef);
                        if (baseNav.MoveToFirstChild())
                        {
                            generateElementBase(nav, baseNav);
                            return;
                        }
                    }
                }
            }

            // No match... try base profile
            // Debug.Print("[generateElementBase] Path = {0}  (no base)".FormatWith(nav.Path));
        }

        /// <summary>Assign the <see cref="ElementDefinition.Base"/> component if necessary.</summary>
        /// <param name="elem">An <see cref="ElementDefinition"/> instance.</param>
        /// <param name="baseElem">The associated base <see cref="ElementDefinition"/> instance.</param>
        /// <param name="isRootElement">
        /// If <c>false</c>, then initialize from baseElem.Base, if it exists.
        /// The root element base component always references to the immediate base profile.
        /// </param>
        private void ensureElementBase(ElementDefinition elem, ElementDefinition baseElem, bool isRootElement = false)
        {
            Debug.Assert(elem != null);
            if (elem.Base == null || (_settings.NormalizeElementBase && !isCreatedBySnapshotGenerator(elem.Base)))
            {
                Debug.Assert(baseElem != null);

                if (!isRootElement && _settings.NormalizeElementBase)
                {
                    if (baseElem.Base != null)
                    {
                        elem.Base = createBaseComponent(
                            baseElem.Base.MaxElement,
                            baseElem.Base.MinElement,
                            baseElem.Base.PathElement
                        );
                    }
                    // [WMR 20160903] Resource has no base
                    else if (!elem.Path.StartsWith("Resource.") && !elem.Path.StartsWith("Element."))
                    {
                        // Generate base component from base element
                        elem.Base = createBaseComponent(
                            baseElem.MaxElement,
                            baseElem.MinElement,
                            baseElem.PathElement
                        );
                    }
                }
                else
                {
                    elem.Base = createBaseComponent(
                        baseElem.MaxElement,
                        baseElem.MinElement,
                        baseElem.PathElement
                    );
                }

                // Debug.Print("[ensureElementBase] #{0} Path = {1}  Base = {2}".FormatWith(elem.GetHashCode(), elem.Path, elem.Base.Path));
                Debug.Assert(elem.Base == null || isCreatedBySnapshotGenerator(elem.Base));
            }
        }

        private ElementDefinition.BaseComponent createBaseComponent(FhirString maxElement, Integer minElement, FhirString pathElement)
        {
            var result = new ElementDefinition.BaseComponent()
            {
                MaxElement = (FhirString)maxElement.DeepCopy(),
                MinElement = (Integer)minElement.DeepCopy(),
                PathElement = (FhirString)pathElement.DeepCopy(),
            };
            result.AddAnnotation(new CreatedBySnapshotGeneratorAnnotation());
            return result;
        }

        // Custom annotation to mark generated elements, so we can prevent duplicate re-generation
        class CreatedBySnapshotGeneratorAnnotation
        {
            private readonly DateTime _created;
            public DateTime Created { get { return _created; } }
            public CreatedBySnapshotGeneratorAnnotation() { _created = DateTime.Now; }
        }

        /// <summary>Determines if the specified element was created by the <see cref="SnapshotGenerator"/> class.</summary>
        /// <param name="elem">A FHIR <see cref="Element"/>.</param>
        /// <returns><c>true</c> if the element was created by the <see cref="SnapshotGenerator"/> class, or <c>false</c> otherwise.</returns>
        private bool isCreatedBySnapshotGenerator(Element elem)
        {
            return elem != null && elem.Annotation<CreatedBySnapshotGeneratorAnnotation>() != null;
        }

    }
}
